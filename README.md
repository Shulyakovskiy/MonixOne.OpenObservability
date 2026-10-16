# MonixOne.OpenObservability

![MonixOne.OpenObservability](assets/openobservability-icon.png)

Центральная телеметрия для ASP.NET Core и .NET 9/10: OpenTelemetry, Sentry,
корреляция запросов, HTTP-логирование с маскированием тел и диагностика исключений.

## Содержание

- [Возможности](#возможности)
- [Подключение](#подключение)
- [Конфигурация OpenTelemetry](#конфигурация-opentelemetry)
- [HTTP-логирование](#http-логирование)
- [Диагностика исключений](#диагностика-исключений)
- [Sentry](#sentry)
- [Расследование инцидентов](#расследование-инцидентов)
- [Разработка и сборка](#разработка-и-сборка)
- [Лицензия](#лицензия)

## Возможности

| Задача | Реализация |
| --- | --- |
| Логи, трассировки и метрики | OpenTelemetry с опциональным OTLP-экспортом |
| Входящие HTTP-запросы и ответы | Штатный ASP.NET Core HTTP Logging |
| Исходящие HTTP-метаданные | `Microsoft.Extensions.Http.Diagnostics` |
| Захват исходящих тел | Пассивное наблюдение через [Nerdbank.Streams.MonitoringStream](https://github.com/dotnet/Nerdbank.Streams) |
| Маскирование JSON и форм | `JsonMasking` и штатный парсер `QueryHelpers.ParseQuery` |
| Диагностика исключений | Автоматическое обогащение обычных вызовов `ILogger` |
| События ошибок | Официальный Sentry SDK |

Пакет связывает `HttpContext.TraceIdentifier`, заголовок `x-correlation-id`,
`ILogger` scopes и Sentry с W3C `Activity.TraceId`.
ASP.NET Core и `HttpClient` используют стандартный `traceparent`.

## Подключение

### NuGet

При центральном управлении версиями добавьте в `Directory.Packages.props`:

```xml
<ItemGroup>
    <PackageVersion Include="MonixOne.OpenObservability" Version="1.0.0" />
</ItemGroup>
```

В проект приложения добавьте ссылку без версии:

```xml
<ItemGroup>
    <PackageReference Include="MonixOne.OpenObservability" />
</ItemGroup>
```

### ASP.NET Core

Зарегистрируйте пакет перед `Build()`. В HTTP pipeline подключите его после routing,
перед существующей обработкой ошибок и аутентификацией приложения.

```csharp
using MonixOne.OpenObservability;

builder.AddOpenObservability();

app.UseRouting();
app.UseOpenObservability();
```

`UseOpenObservability()` подключает корреляцию и штатный HTTP Logging.
Повторный `AddOpenObservability()` не дублирует обработчики и exporters.
Ранее зарегистрированные `ILogger` providers сохраняются.

### Generic host

Для worker services и консольных приложений достаточно регистрации в host:

```csharp
using Microsoft.Extensions.Hosting;
using MonixOne.OpenObservability;

var builder = Host.CreateApplicationBuilder(args);
builder.AddOpenObservability();

using var host = builder.Build();
await host.RunAsync();
```

Lifecycle host управляет запуском и завершением SDK, включая flush при остановке.

## Конфигурация OpenTelemetry

### appsettings.json

```json
{
  "Service": {
    "Name": "identity-service"
  },
  "Observability": {
    "Logging": {
      "Enabled": true,
      "OtlpEndpoint": "http://otel-collector:4318",
      "OtlpProtocol": "http/protobuf",
      "ConsoleEnabled": false
    }
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  }
}
```

| Параметр | Назначение | По умолчанию |
| --- | --- | --- |
| `Service:Name` | Имя сервиса в телеметрии | Имя приложения из host environment |
| `Observability:Logging:Enabled` | Включение OTLP-экспорта logs, traces и metrics | `false` |
| `Observability:Logging:OtlpEndpoint` | Базовый HTTP/HTTPS endpoint коллектора | Не задан |
| `Observability:Logging:OtlpProtocol` | `http/protobuf`, `http` или `grpc` | `http/protobuf` |
| `Observability:Logging:OtlpHeaders` | Заголовки авторизации OTLP-экспорта | `OTEL_EXPORTER_OTLP_HEADERS`, если задан |
| `Observability:Logging:ConsoleEnabled` | Включение штатного console provider | `true` для Development/Local, иначе `false` |

Секция `Observability:Logging` сохранена для совместимости существующих приложений.
`Enabled` управляет OTLP-экспортом всех трёх сигналов. Невалидный или пустой endpoint
отключает этот экспорт; локальные логи продолжают работать.
Sentry включается отдельно через [DSN](#sentry).

Версия сервиса определяется по entry assembly, окружение — по host environment.

### Переменные окружения

В именах вложенных параметров используется `__`:

```dotenv
Service__Name=identity-service
Observability__Logging__Enabled=true
Observability__Logging__OtlpEndpoint=http://otel-collector:4318
Observability__Logging__OtlpProtocol=http/protobuf
Observability__Logging__ConsoleEnabled=false
```

Заголовки OTLP задавайте через `Observability__Logging__OtlpHeaders`
или `OTEL_EXPORTER_OTLP_HEADERS`. Секретные значения храните вне репозитория.

### Сигналы и метрики

При `http/protobuf` пакет добавляет к базовому endpoint путь соответствующего сигнала:

| Сигнал | Путь |
| --- | --- |
| Логи | `/v1/logs` |
| Трассировки | `/v1/traces` |
| Метрики | `/v1/metrics` |

Собираются стандартные метрики .NET 9/10 из meters:

- `Microsoft.AspNetCore.Hosting`;
- `Microsoft.AspNetCore.Server.Kestrel`;
- `System.Net.Http`;
- `System.Net.NameResolution`;
- `System.Runtime`.

### Уровни HTTP-логирования

Пакет задаёт `Information` для своих HTTP-категорий, в том числе при общем
`Microsoft=Warning`. Явные правила для этих категорий в `Logging:LogLevel`
сохраняют приоритет.

Например, исходящий захват тел можно отключить отдельно:

```json
{
  "Logging": {
    "LogLevel": {
      "MonixOne.OpenObservability.HttpBodyLoggingHandler": "None"
    }
  }
}
```

## HTTP-логирование

### Входящие и исходящие вызовы

Входящие запросы и ответы захватывает штатный `AddHttpLogging()` / `UseHttpLogging()`.
Запрос и ответ объединяются в одну запись с итоговым status, route template и duration.

Исходящие метаданные логирует `AddExtendedHttpClientLogging()` для всех клиентов
`IHttpClientFactory`, включая Refit и OAuth backchannel.
Тела передаются в лог через адаптер `HttpContent` и библиотечный `MonitoringStream`.

Захват пассивный: учитываются только отправленные или прочитанные байты.
Сохраняются cancellation, клиентские таймауты, заголовки, `Content-Length`
и владение потоком. Дополнительного чтения и отдельных body-таймаутов нет.

### Форматы и маскирование

Поддерживаются:

- `application/json`;
- `application/*+json`;
- `application/x-www-form-urlencoded`.

Рекурсивное маскирование выполняет [JsonMasking](https://github.com/ThiagoBarradas/jsonmasking).
Правила работают без учёта регистра, на любой глубине объектов и массивов.

Маскируются пароли, токены, client secrets, API keys, OTP, authorization codes/verifiers,
контакты, credentials/assertions/challenges, SMS text/destination и персональные поля.
Поля `detail`, `message` и `error_description` скрываются целиком: внешние сервисы
могут отражать в них секреты из запроса.

Пример очищенного тела:

```json
{
  "request_id": "req-42",
  "access_token": "[REDACTED]",
  "profile": {
    "email": "[REDACTED]",
    "safe": "visible"
  }
}
```

Формы декодируются через `QueryHelpers.ParseQuery` и логируются как очищенный JSON,
включая повторяющиеся и percent-encoded поля.
Очищенные body-атрибуты и текст получают все providers: Console, OpenTelemetry
и дополнительные провайдеры приложения.

### Лимиты и неполные тела

| Ограничение | Значение |
| --- | --- |
| Лимит захвата тела | 16 КБ |
| Лимит очищенного тела | 16 КБ |
| Максимальная глубина JSON | 32 |

Пустое тело остаётся пустым. Невалидные, обрезанные, слишком большие
и неподдерживаемые тела получают `[omitted: reason]`.
Неполное чтение получает `incomplete_body` вместо захваченного префикса.
Бинарные потоки, файлы и неструктурированный текст не включаются в содержимое body-логов.

Маскирование зависит от имён полей. Секрет в произвольно названном поле не может
надёжно распознаваться автоматически; новые контракты нужно сверять с политикой пакета.
Сообщения и структурированные поля бизнес-логов также должны исключать секреты.

### Заголовки и маршруты

| Направление | Разрешённые значения заголовков |
| --- | --- |
| Запрос | `Accept`, `Content-Type`, `Content-Length`, `Content-Encoding`, `traceparent`, `x-correlation-id`, `x-request-id` |
| Ответ | `Content-Type`, `Content-Length`, `Content-Encoding`, `Retry-After`, `traceparent`, `x-correlation-id`, `x-request-id` |

Значения `Authorization`, `Cookie`, `Set-Cookie` и API keys не выводятся.
Query string и значения route-параметров скрываются; входящие маршруты представлены
шаблонами, исходящие параметры редактируются в режиме `Strict`.
Refit дополнительно предоставляет `http_route`.

Штатные enrichment callbacks OpenTelemetry удаляют raw URL/path/query из HTTP spans,
сохраняя method, route template, status и корреляцию.
Адаптер `ILogger` очищает URL-компоненты сообщений `Hosting.Diagnostics`
и `RequestPath` в его scopes, включая уровень `Information`.

Входящие `/api/health` и его вложенные маршруты исключены из HTTP Logging
и ASP.NET Core tracing.

### Потоковые ответы и ограничения клиентов

`ResponseHeadersRead` возвращает ответ после заголовков. Содержимое тела появляется
в логе после чтения, копирования или освобождения content/потока.
Исходящие body-события сохраняют `trace_id` и `span_id` исходного HTTP-запроса,
включая чтение после завершения его `Activity`.

Для захвата исходящих тел используйте `IHttpClientFactory` и `SendAsync()`.
Прямой `new HttpClient()` обходит регистрацию захвата тел.
Штатный Microsoft HTTP logger отклоняет синхронный `HttpClient.Send()`;
синхронное чтение ответа после `SendAsync()` поддерживается.

## Диагностика исключений

### Обычные вызовы ILogger

Передайте исключение в стандартный вызов — пакет добавит диагностику автоматически:

```csharp
logger.LogError(
    exception,
    "External service request failed for {request_id}",
    requestId);
```

Для стабильного имени бизнес-события используйте `LogEvent()`:

```csharp
logger.LogEvent(
    LogLevel.Error,
    "external.request.failed",
    exception,
    "External service request failed for {request_id}",
    requestId);
```

### Диагностические поля

| Поле | Содержимое |
| --- | --- |
| `exception_type` | Тип переданного исключения |
| `exception_inner_type` | Тип непосредственного `InnerException` |
| `exception_root_type` | Тип корневого исключения |
| `exception_stack_trace` | Типы и стеки цепочки без сообщений исключений |
| `failure_kind` | Классификация сбоя |
| `status_code` | HTTP status из Refit `ApiException` или `HttpRequestException` |
| `json_error_line` | Строка ошибки JSON |
| `json_error_byte_position` | Позиция ошибки JSON в байтах |
| `http_method`, `http_route`, `http_status_code` | Метод, шаблон маршрута и текущий response status при доступном HTTP-контексте |

`JsonException` распознаётся во всей `InnerException`-цепочке и во всех ветках
`AggregateException`, в том числе при вложенном `FormatException`.

### Классификация сбоев

Классификация выбирается в следующем порядке:

| failure_kind | Условие |
| --- | --- |
| `response_deserialization` | Найден `JsonException`, в том числе при HTTP 200 |
| `timeout` | Найден `TimeoutException` или Polly `TimeoutRejectedException` |
| `canceled` | Найден `OperationCanceledException` |
| `http_status` | HTTP status не меньше 400 |
| `transport` | Найден `HttpRequestException` без предыдущих условий |
| `application` | Остальные исключения |

Сырые объекты `Exception` не передаются providers: `ApiException.ToString()` может
содержать полное HTTP-тело. Экспортируются типы, классификация и стеки без
`Exception.Message` / `ToString()`.

### Дополнительные расширения

Если исключение не передаётся в `ILogger`, диагностику можно добавить через scope:

```csharp
using var scope = logger.BeginExceptionDiagnostics(exception);
logger.LogWarning("Operation rejected");
```

`exception.GetHttpStatusCode()` доступен отдельно для интеграционного кода.

## Sentry

### Включение через DSN

Для включения Sentry задайте DSN проекта через `Sentry:Dsn` или переменную
окружения `Sentry__Dsn`. Также поддерживается `SENTRY_DSN`:

```dotenv
Sentry__Dsn=https://public-key@sentry.example.com/1
```

Это пример формата DSN; подставьте значение из настроек своего Sentry-проекта.
Для `SENTRY_DSN` действует fallback: пакет использует его, если `Sentry:Dsn`
не задан или пуст. При непустом `Sentry:Dsn` приоритет остаётся за ним.

Если оба значения пусты, Sentry не подключается. OpenTelemetry и локальные логи
продолжают работать согласно своим настройкам.

### appsettings.json

DSN — единственный обязательный параметр для включения интеграции.
Уровень событий и sampling можно задать дополнительно:

```json
{
  "Sentry": {
    "Dsn": "",
    "MinimumEventLevel": "Error",
    "TracesSampleRate": 0.0
  }
}
```

В этом примере DSN приходит из окружения, Sentry получает события уровня `Error`
и выше, а performance tracing Sentry выключен.

| Параметр | Назначение |
| --- | --- |
| `Dsn` | Адрес проекта для отправки событий |
| `MinimumEventLevel` | Минимальный уровень события `ILogger`, например `Error` или `Critical` |
| `TracesSampleRate` | Доля Sentry transactions: `0.0` — tracing выключен, `0.1` — sampling 10%, `1.0` — sampling 100% |

Событие должно пройти и фильтр категории `ILogger`, и `Sentry:MinimumEventLevel`.
`TracesSampleRate` не меняет отправку ошибок и настройки трассировок OpenTelemetry.

Эквивалентные настройки через окружение:

```dotenv
Sentry__Dsn=https://public-key@sentry.example.com/1
Sentry__MinimumEventLevel=Error
Sentry__TracesSampleRate=0.0
```

### Отправка событий и очистка данных

Для ASP.NET Core пакет регистрирует официальный `UseSentry()`,
для generic host — Sentry logging provider.
Обычные `LogError()` / `LogCritical()` отправляют очищенные события;
ручной `CaptureException()` в сервисах не требуется.

Штатные exception processors SDK сохраняют типы исключений и stack frames.
Перед отправкой пакет очищает:

- request/user данные и сообщения исключений;
- значения локальных переменных stack frames;
- `Exception.Data`, включая проекции в mechanism, tags и contexts;
- HTTP URL в span data/tags, HTTP span descriptions и URL transaction names.

Пакет задаёт следующие настройки поверх секции `Sentry`:

| Настройка | Значение |
| --- | --- |
| `Environment` | Окружение приложения из host environment |
| `SendDefaultPii` | `false` |
| `MaxBreadcrumbs` | `0` |
| `MaxRequestBodySize` для ASP.NET Core | `None` |
| `ShutdownTimeout` | 5 секунд |

HTTP-тела доступны в очищенных OpenTelemetry-логах.
Завершение SDK и flush выполняются через lifecycle host.

## Расследование инцидентов

1. Найдите бизнес-ошибку по `event_name` и определите её `trace_id`.
2. Найдите входящий HTTP-лог и spans с тем же `trace_id`.
3. Сопоставьте исходящие HTTP-метаданные и body-события.
4. Проверьте status, content type/length, `RequestBody`, `ResponseBody` и `failure_kind`.
5. Для ошибок JSON проверьте позицию десериализации; для остальных сбоев — типы и стеки,
   в том числе в связанном событии Sentry.

| event_name | Данные |
| --- | --- |
| `http.request.completed` | Входящий запрос/ответ, итоговый status и `duration_ms` |
| `http.client.request.body` | Очищенное тело исходящего запроса |
| `http.client.response.body` | Очищенное тело исходящего ответа |

## Разработка и сборка

### Структура решения

```text
src/MonixOne.OpenObservability/        Исходный код пакета
tests/MonixOne.OpenObservability.Tests/ Проверки .NET 9/10
assets/                              Иконка пакета
Directory.Build.props                Общие настройки сборки и анализаторов
Directory.Packages.props             Центральные версии зависимостей
global.json                          SDK 10 и Microsoft.Testing.Platform
.github/workflows/                   CI и публикация
```

Все `PackageReference` используют центральные версии через
`ManagePackageVersionsCentrally`. В Solution Items собраны настройки editor/git,
build/package props, SDK, README, лицензия, иконка и workflows.

### Локальные проверки

Команды выполняются из корня решения:

```sh
dotnet restore MonixOne.OpenObservability.sln

dotnet build MonixOne.OpenObservability.sln --configuration Release --no-restore \
  -p:GeneratePackageOnBuild=false

dotnet test \
  --project tests/MonixOne.OpenObservability.Tests/MonixOne.OpenObservability.Tests.csproj \
  --configuration Release --no-build --no-restore
```

### Сборка NuGet

```sh
dotnet pack src/MonixOne.OpenObservability/MonixOne.OpenObservability.csproj \
  --configuration Release --no-build --no-restore \
  --output artifacts/nuget -p:GeneratePackageOnBuild=false
```

Результат — `.nupkg` и `.snupkg` в `artifacts/nuget`.

### CI и публикация

CI собирает Release, запускает проверки на .NET 9/10 и создаёт NuGet-артефакты.
Publish workflow запускается после merge pull request в `main` и использует
GitHub environment `production`, NuGet trusted publishing и секрет `NUGET_USER`.

Для публикации должны быть настроены trusted publisher в NuGet
и соответствующее окружение GitHub.

## Лицензия

[MIT](LICENSE).
