# Архитектура

Документ объясняет, **почему** проект устроен именно так. Конвенции для агентов — в
[../AGENTS.md](../AGENTS.md), обзор — в [../README.md](../README.md).

## Главное ограничение

Tekla Open API — это набор **.NET-сборок под Windows** (`.NET Framework 4.8`, только x64
начиная с Tekla 2026). Разработка же ведётся на **macOS, где Tekla нет** и где её нельзя
установить. Значит, нужна архитектура, которая:

1. позволяет писать и **запускать** код на Mac (хотя бы каркас MCP);
2. переносится на Windows и там работает с реальной Tekla без переписывания.

## Решение: интерфейс + две реализации + мультитаргет

### Слой абстракции
`ITeklaModelService` (в `TeklaMcp.Core`) описывает все операции чтения. MCP-инструменты
знают только про него. Две реализации:

- `MockTeklaModelService` — фейковые данные, кросс-платформенно (`netstandard2.0`).
- `TeklaModelService` — реальный Tekla Open API (`net48`, Windows-only).

### Почему `netstandard2.0` для Core и Mock
`netstandard2.0` — общий знаменатель: его понимают и современный `.NET 8`, и старый
`.NET Framework 4.8`. Поэтому одни и те же `Core`/`Mock` подключаются в обе сборки сервера.

### Почему мультитаргет сервера `net8.0; net48`
Ключевое совпадение, которое всё упрощает:

| Компонент | Поддерживаемые таргеты |
|---|---|
| MCP C# SDK (`ModelContextProtocol`) | `netstandard2.0`, `net8.0` |
| Tekla Open API 2026 | `.NET Framework 4.8`, `netstandard2.0` |

Оба совместимы с `netstandard2.0`, поэтому **один процесс `net48` на Windows может
одновременно хостить и MCP SDK, и Tekla**. Разносить на два процесса не нужно. Отсюда:

- **`net8.0`** — сборка для Mac/разработки. Только `Core` + `Mock`. Запускается везде.
- **`net48`** — сборка для Windows. Дополнительно подключает `TeklaMcp.Tekla` и реальный
  бэкенд. Собирается только на Windows.

Выбор бэкенда — через `#if NET48` в `Program.cs`. Плюс переменная `TEKLA_MCP_USE_MOCK=1`
форсит мок даже в `net48`-сборке (удобно для проверки без открытой модели).

### Почему `net48`-таргет отключается на не-Windows
В `TeklaMcp.Server.csproj`:
```xml
<TargetFrameworks Condition="'$(OS)' == 'Windows_NT'">net8.0;net48</TargetFrameworks>
<TargetFrameworks Condition="'$(OS)' != 'Windows_NT'">net8.0</TargetFrameworks>
```
Так на Mac сервер собирается только как `net8.0` и **не тянет** Windows-only проект
`TeklaMcp.Tekla` (который ссылается на Tekla-сборки, недоступные на Mac).

## Транспорт MCP: stdio

Сервер общается с клиентом по **stdio** (JSON-RPC через stdin/stdout). Поэтому:
- stdout зарезервирован под протокол — логи идут **только в stderr**
  (`LogToStandardErrorThreshold` в `Program.cs`);
- клиент сам запускает процесс сервера (см. конфиг в README).

HTTP/SSE-транспорт можно добавить позже (пакет `ModelContextProtocol.AspNetCore`), но для
локального инструмента рядом с Tekla stdio проще и безопаснее.

## Поток данных одного вызова

```
Клиент → JSON-RPC (stdin) → MCP SDK → метод инструмента [tekla_*]
       → ITeklaModelService (Mock | Tekla)
       → (на Windows) Tekla Open API → открытая модель
       → DTO (TeklaMcp.Core.Models.*) → JSON → (stdout) → клиент
```

## Запасной план B: два процесса

Если на `net48` MCP SDK будет конфликтовать по зависимостям (классическая боль .NET
Framework: `System.Text.Json`, binding redirects), запасная архитектура:

- MCP-сервер остаётся `net8.0` (кросс-платформенный);
- появляется отдельный маленький `net48`-воркер, который ссылается на Tekla и общается
  с сервером по stdio/именованным каналам.

Это надёжнее по изоляции, но сложнее. Для прототипа выбран одно-процессный вариант; план B
описан, чтобы будущий агент не изобретал его заново. Подробности и риск — в
[tekla-api-notes.md](tekla-api-notes.md).
