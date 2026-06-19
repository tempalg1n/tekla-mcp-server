# Tekla MCP Server

MCP-сервер для **Tekla Structures (проверено на 2023)**: даёт ИИ-ассистентам (Claude и др.) инструменты
для **чтения и анализа** элементов открытой модели через
[Tekla Open API](https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304).

> **Статус: рабочий прототип.** Сервер собирается и запускается на Windows, проверен
> на живой модели в Tekla 2023. Набор инструментов уже покрывает базовые запросы по
> фильтрации, весам, группировкам и выделению объектов в UI.

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
| `TeklaModelService` | только Windows + Tekla, `net48` | реальные данные из открытой модели (проверено на Tekla 2023) |

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

Основные инструменты для чтения/аналитики, UI-выделения и работы с UDA.
Имена с префиксом `tekla_`.

| Инструмент | Назначение |
|---|---|
| `tekla_get_connection_info` | Проверить связь: есть ли открытая модель, имя/путь, активный бэкенд. |
| `tekla_get_model_summary` | Сводка по всей модели: кол-во объектов, общий вес, разбивки по типу/классу/профилю/материалу. |
| `tekla_list_objects` | Список объектов с основными свойствами (с лимитом). |
| `tekla_find_objects` | Поиск по фильтрам: тип, класс, профиль, материал, имя. |
| `tekla_get_object_by_guid` | Один объект по GUID. |
| `tekla_get_selected_objects` | Что пользователь выделил в UI Tekla. |
| `tekla_analyze_by_material` | Разбивка «ведомость металла» по маркам стали (кол-во + вес). |
| `tekla_count_objects` | Подсчёт количества объектов по фильтрам (тип/класс/профиль/материал/имя). |
| `tekla_sum_weight` | Подсчёт суммарного веса по фильтрам (включая кол-во объектов с весом). |
| `tekla_group_weight_by` | Группировка count + weight по полю (`type`, `class`, `profile`, `material`, `name`). |
| `tekla_list_distinct_values` | Список уникальных значений поля с count + weight. |
| `tekla_select_objects` | Выделить объекты в UI Tekla по фильтру и вернуть `selectedCount` + preview. |
| `tekla_get_object_udas` | Прочитать UDA-поля у конкретного объекта по GUID. |
| `tekla_set_object_udas` | Записать/изменить UDA у объекта по GUID (`apply=false` по умолчанию). |
| `tekla_set_udas_by_filter` | Массово записать/изменить UDA по фильтру (`apply=false` по умолчанию). |

### Ключевые аргументы фильтрации

Большинство query/analytics инструментов поддерживают один и тот же набор фильтров:

- `type` — точное совпадение типа (`Beam`, `ContourPlate`, `Bolt`, ...);
- `class` — точное совпадение класса;
- `profile` — подстрока по профилю;
- `material` — подстрока по материалу;
- `nameContains` — подстрока по имени.

### Примеры команд к LLM

- «Посчитай вес только колонн (тип `Beam`, имя содержит `Колонна`)».
- «Посчитай вес только настила (тип `ContourPlate`)».
- «Покажи топ материалов по весу в текущей модели».
- «Сгруппируй балки по профилю и покажи вес по каждой группе».
- «Сколько элементов класса 20 в модели?».
- «Выдели в Tekla все элементы класса 20».
- «Найди все объекты из материала `C245` и выведи первые 30».
- «Возьми выделенные элементы и посчитай их суммарный вес».
- «Прочитай UDA `USER_FIELD_1` и `USER_PHASE` у объекта с таким GUID».
- «Превью: проставь `USER_FIELD_1=KMD;USER_PHASE=2` для всех колонн профиля `I30K1`, но без применения».
- «Применить: проставь те же UDA для колонн профиля `I30K1` (`apply=true`)».

### Безопасность UDA-записи

`tekla_set_object_udas` и `tekla_set_udas_by_filter` сделаны с безопасным поведением:

- по умолчанию `apply=false` — это только preview (без изменений в модели);
- чтобы реально записать UDA, нужно явно передать `apply=true`;
- для массовой записи есть ограничение `limit` (по умолчанию 200 объектов).

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

Требования: Windows x64, **Tekla Structures** установлена и **открыта с моделью**,
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

## Что ещё проверить на Windows

Базовый сценарий уже проверен на Tekla 2023 (подключение, summary, list/find, select).
Для следующих итераций всё ещё полезно перепроверять:

- report-свойства (`WEIGHT`, `LENGTH`, `ASSEMBLY_POS`) и единицы измерения на ваших шаблонах;
- корректность `SelectModelObject(Identifier)` на разных типах объектов;
- поведение `tekla_select_objects` на больших выборках;
- соответствие версии `Tekla.Structures.*` версии установленной Tekla.

Подробные заметки — в [docs/tekla-api-notes.md](docs/tekla-api-notes.md).

---

## Дорожная карта

- [ ] Скомпилировать и запустить мок-сервер на Mac (проверить MCP-обвязку).
- [ ] Скомпилировать `net48`-сборку на Windows, поднять связь с Tekla.
- [ ] Проверить и поправить вызовы Tekla Open API на живой модели.
- [ ] Расширить чтение: болты, сборки (assemblies), арматура, UDA, геометрия.
- [x] Добавлены базовые операции записи UDA с защитой `apply=false` по умолчанию.

---

## Для ИИ-агентов

**Перед любыми изменениями прочитайте [AGENTS.md](AGENTS.md)** — там конвенции, карта
кода, где можно/нельзя ломать совместимость и как тестировать на Mac vs Windows.
