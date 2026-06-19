# Tekla MCP Server

MCP-сервер для **Tekla Structures 2026**: даёт ИИ-ассистентам (Claude и др.) инструменты
для **чтения и анализа** элементов открытой модели через
[Tekla Open API](https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304).

> **Статус: ранний прототип (каркас).** Цель этого этапа — рабочая структура и набор
> read-only инструментов, которые **запускаются**. Реальная Tekla-часть написана по
> документации и **ещё не компилировалась и не проверялась на живой Tekla** — см.
> [«Что точно требует проверки»](#что-точно-требует-проверки-на-windows).

---

## Зачем это и как устроено

Tekla Open API — это **.NET-сборки под Windows** (`.NET Framework 4.8`, x64), работающие
внутри/рядом с процессом Tekla Structures. Поэтому сервер написан на **C#**. Но разработка
идёт на **macOS без Tekla**, поэтому проект устроен так, чтобы **запускаться и на Mac на
фейковых данных**, и на рабочем Windows-компе на реальной модели.

Ключ к этому — интерфейс `ITeklaModelService` с двумя реализациями:

| Реализация | Где работает | Что отдаёт |
|---|---|---|
| `MockTeklaModelService` | где угодно (Mac/Linux/Windows), `net8.0` | синтетический стальной каркас (~42 объекта) |
| `TeklaModelService` | только Windows + Tekla, `net48` | реальные данные из открытой модели |

MCP-инструменты зависят только от интерфейса, поэтому **один и тот же набор инструментов**
работает с любым бэкендом.

```
┌──────────────┐  stdio (JSON-RPC)   ┌───────────────────────────────────────┐
│  MCP-клиент  │ ──────────────────► │            TeklaMcp.Server            │
│ (Claude и    │                     │  MCP-инструменты (tekla_*)            │
│  др.)        │ ◄────────────────── │            │                          │
└──────────────┘                     │            ▼  ITeklaModelService      │
                                     │   ┌─────────────────┐  ┌────────────┐ │
                                     │   │ Mock (net8.0)   │  │Tekla(net48)│ │
                                     │   │ фейк-данные     │  │реальн. API │ │
                                     │   └─────────────────┘  └─────┬──────┘ │
                                     └──────────────────────────────┼────────┘
                                                                    ▼
                                                       Tekla Structures 2026
```

Подробнее — [docs/architecture.md](docs/architecture.md).

---

## Структура репозитория

```
tekla-mcp/
├── README.md                  ← этот файл
├── AGENTS.md                  ← инструкция для ИИ-агентов (читать первым!)
├── docs/
│   ├── architecture.md        ← архитектура и обоснование решений
│   ├── tekla-api-notes.md     ← заметки по Tekla Open API + что проверить
│   └── dotnet-for-python-devs.md ← шпаргалка по C#/.NET для питониста
├── TeklaMcp.sln               ← решение (всё) — собирать на Windows
├── TeklaMcp.Mac.slnf          ← фильтр без Tekla-проекта — для сборки на Mac
└── src/
    ├── TeklaMcp.Core/         ← интерфейс + DTO (netstandard2.0)
    ├── TeklaMcp.Mock/         ← фейковая реализация (netstandard2.0)
    ├── TeklaMcp.Tekla/        ← реальная реализация (net48, Windows-only)
    └── TeklaMcp.Server/       ← MCP-хост + инструменты (net8.0; +net48 на Windows)
```

---

## Инструменты (tools)

Все read-only. Имена с префиксом `tekla_`.

| Инструмент | Назначение |
|---|---|
| `tekla_get_connection_info` | Проверить связь: есть ли открытая модель, имя/путь, активный бэкенд. |
| `tekla_get_model_summary` | Сводка по всей модели: кол-во объектов, общий вес, разбивки по типу/классу/профилю/материалу. |
| `tekla_list_objects` | Список объектов с основными свойствами (с лимитом). |
| `tekla_find_objects` | Поиск по фильтрам: тип, класс, профиль, материал, имя. |
| `tekla_get_object_by_guid` | Один объект по GUID. |
| `tekla_get_selected_objects` | Что пользователь выделил в UI Tekla. |
| `tekla_analyze_by_material` | Разбивка «ведомость металла» по маркам стали (кол-во + вес). |

---

## Запуск на macOS (фейковые данные, без Tekla)

Нужен **.NET SDK 8+** (Tekla не нужна). Если SDK ещё нет:

```bash
brew install --cask dotnet-sdk     # или скачать с https://dotnet.microsoft.com/download
dotnet --version                   # должно показать 8.x или новее
```

Сборка и запуск сервера с мок-бэкендом:

```bash
# на Mac TargetFramework у сервера = только net8.0, Tekla-проект не подтягивается
dotnet run --project src/TeklaMcp.Server
```

Сервер общается по stdio и сам по себе «висит» в ожидании MCP-клиента — это нормально.
Чтобы проверить, что инструменты реально вызываются, подключите его к MCP-клиенту (ниже)
или к MCP Inspector:

```bash
npx @modelcontextprotocol/inspector dotnet run --project src/TeklaMcp.Server
```

---

## Запуск на Windows (реальная Tekla)

Требования: Windows x64, **Tekla Structures 2026** установлена и **открыта с моделью**,
**.NET SDK 8+** и таргет-пак **.NET Framework 4.8 Developer Pack**.

```powershell
# Откройте Tekla Structures и загрузите модель, затем:
dotnet build TeklaMcp.sln -c Release
# Запуск .NET Framework сборки, которая разговаривает с Tekla:
dotnet run --project src/TeklaMcp.Server -f net48 -c Release
```

Принудительно использовать мок даже на Windows (например, без открытой модели):

```powershell
$env:TEKLA_MCP_USE_MOCK = "1"
dotnet run --project src/TeklaMcp.Server -f net48
```

---

## Подключение к MCP-клиенту

Пример конфигурации (Claude Desktop / Claude Code, `mcpServers`). На **Mac** (мок):

```json
{
  "mcpServers": {
    "tekla": {
      "command": "dotnet",
      "args": ["run", "--project", "/абсолютный/путь/tekla-mcp/src/TeklaMcp.Server"]
    }
  }
}
```

На **Windows** (реальная Tekla) — лучше указывать на собранный `.exe`, а не `dotnet run`:

```json
{
  "mcpServers": {
    "tekla": {
      "command": "C:\\путь\\tekla-mcp\\src\\TeklaMcp.Server\\bin\\Release\\net48\\TeklaMcp.Server.exe"
    }
  }
}
```

---

## Что точно требует проверки на Windows

Реальная реализация ([TeklaModelService.cs](src/TeklaMcp.Tekla/TeklaModelService.cs))
написана по докам и **не проверена**. Наиболее вероятные места для правок:

- имена report-свойств (`WEIGHT`, `LENGTH`, `ASSEMBLY_POS`) и единицы измерения;
- `model.GetInfo()` и поля `ModelInfo` (`ModelName`, `ModelPath`);
- `model.SelectModelObject(Identifier)` для поиска по GUID;
- версии NuGet-пакетов `Tekla.Structures.*` (сейчас `2026.0.3` — сверить с установленной);
- совместимость MCP SDK с `net48` (может потребоваться правка версий / binding redirects).

Полный чек-лист и ссылки — в [docs/tekla-api-notes.md](docs/tekla-api-notes.md).

---

## Дорожная карта

- [ ] Скомпилировать и запустить мок-сервер на Mac (проверить MCP-обвязку).
- [ ] Скомпилировать `net48`-сборку на Windows, поднять связь с Tekla.
- [ ] Проверить и поправить вызовы Tekla Open API на живой модели.
- [ ] Расширить чтение: болты, сборки (assemblies), арматура, UDA, геометрия.
- [ ] Позже — операции записи (создание/изменение), под отдельным флагом безопасности.

---

## Для ИИ-агентов

**Перед любыми изменениями прочитайте [AGENTS.md](AGENTS.md)** — там конвенции, карта
кода, где можно/нельзя ломать совместимость и как тестировать на Mac vs Windows.
