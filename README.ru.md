# Tekla MCP Server

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[MCP](https://modelcontextprotocol.io/)-сервер, который подключает ИИ-ассистентов к моделям **Tekla Structures** через [Tekla Open API](https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304).

Задавайте вопросы о металлоконструкциях на естественном языке — вес, количество, материалы, профили, выделение — и ассистент запросит данные из открытой модели.

> **Статус: активная разработка.** Сервер собирается и работает на Windows, проверен на живых моделях Tekla 2023. Набор инструментов расширяется; API и поведение могут меняться между релизами. См. [Дорожную карту](#дорожная-карта).

**Языки:** [English](README.md) · Русский (этот файл)

---

## Зачем этот проект

Tekla Structures хранит богатые BIM-данные — детали, сборки, веса, классы, пользовательские атрибуты — но доступ к ним возможен только через Windows-only .NET API. ИИ-ассистентам нужен структурированный и безопасный мост для чтения (и выборочного изменения) данных модели без ручного экспорта и макросов.

Этот сервер — такой мост через **Model Context Protocol**: стандартный способ для Claude, Cursor и других MCP-клиентов вызывать типизированные инструменты к открытой модели Tekla.

**Что уже можно делать:**

- Проверять подключение, имя модели и количество объектов
- Фильтровать и искать детали по типу, классу, профилю, материалу и имени
- Фильтровать по UDA и произвольным атрибутам, а также искать имя атрибута по значению
- Считать вес и количество с единым набором фильтров
- Группировать метрики по полю (тип, класс, профиль, материал, имя)
- Строить разбивку по материалам (ведомость металла)
- Читать текущее выделение в UI Tekla
- Выделять объекты в UI Tekla по фильтру
- Анализировать уникальные типы соединений для заданного профиля
- Читать и записывать UDA с безопасным preview по умолчанию

---

## Архитектура

Сервер написан на **C#**, потому что Tekla Open API поставляется как .NET-сборки (`.NET Framework 4.8`, x64). Все MCP-инструменты зависят от одной абстракции — `ITeklaModelService` — с двумя реализациями:

| Реализация | Сборка | Назначение |
|---|---|---|
| `MockTeklaModelService` | `net8.0` | Синтетический стальной каркас (~42 объекта) для разработки без Tekla |
| `TeklaModelService` | `net48` (Windows) | Данные из открытой модели (проверено на Tekla 2023) |

Подробнее — [docs/architecture.md](docs/architecture.md) (на английском).

---

## Инструменты

Все инструменты с префиксом `tekla_`. Полная таблица — в [английском README](README.md#tools).

| Инструмент | Назначение |
|---|---|
| `tekla_get_connection_info` | Проверка подключения к модели |
| `tekla_get_model_summary` | Сводка по модели |
| `tekla_list_objects` / `tekla_find_objects` | Список и поиск объектов |
| `tekla_count_objects` / `tekla_sum_weight` | Подсчёт и суммарный вес |
| `tekla_group_weight_by` / `tekla_list_distinct_values` | Группировки и уникальные значения |
| `tekla_analyze_by_material` | Разбивка по материалам |
| `tekla_find_attributes_by_value` | Поиск имени атрибута по известному значению |
| `tekla_analyze_profile_connections` | Анализ уникальных типов узлов для профиля |
| `tekla_get_selected_objects` / `tekla_select_objects` | Чтение и установка выделения в UI |
| `tekla_get_object_udas` / `tekla_set_object_udas` / `tekla_set_udas_by_filter` | Работа с UDA |
| `tekla_search_api` / `tekla_get_api_doc` | Поиск по локальному справочнику Tekla Open API (типы и сигнатуры) |
| `tekla_run_csharp` | Escape hatch: выполнить короткий C#-скрипт с полным Tekla Open API (read-only по умолчанию, проверка политикой, таймаут) |

Запись UDA по умолчанию в режиме **preview** (`apply=false`); для применения передайте `apply=true`.
Мутации из скриптов требуют `allowMutations=true` **и** запуска сервера с `TEKLA_MCP_ALLOW_SCRIPT_WRITES=1`.

---

## Требования

**Работа с живой моделью:**

- Windows x64
- Установленная Tekla Structures, **запущена с открытой моделью**
- [.NET SDK 8+](https://dotnet.microsoft.com/download)
- [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48)

**Разработка / тестирование (мок, без Tekla):**

- [.NET SDK 8+](https://dotnet.microsoft.com/download)

---

## Быстрый старт

### Windows — живая модель Tekla

```powershell
dotnet build TeklaMcp.sln -c Release
dotnet run --project src/TeklaMcp.Server -f net48 -c Release
```

### Мок-бэкенд (без Tekla)

```bash
dotnet run --project src/TeklaMcp.Server
```

Интерактивная проверка:

```bash
npx @modelcontextprotocol/inspector dotnet run --project src/TeklaMcp.Server
```

---

## Подключение MCP-клиента

```json
{
  "mcpServers": {
    "tekla": {
      "command": "C:\\path\\to\\tekla-mcp-server\\src\\TeklaMcp.Server\\bin\\Release\\net48\\TeklaMcp.Server.exe"
    }
  }
}
```

Для мок-бэкенда используйте `dotnet run --project …/src/TeklaMcp.Server`.

---

## Дорожная карта

- [x] Базовое чтение: подключение, сводка, список, поиск, выделение
- [x] Аналитика: count, weight, группировки, материалы
- [x] Программное выделение в UI Tekla
- [x] UDA с preview по умолчанию
- [ ] Расширение: болты, сборки, арматура, геометрия
- [ ] Автотесты для Core и Mock
- [ ] Матрица совместимости Tekla 2023–2026

---

## Авторство

Проект **спроектирован и разработан с помощью [Claude Opus 4.8](https://www.anthropic.com/claude)** (Anthropic) — архитектура, реализация и документация с участием ИИ.

Tekla Structures — продукт [Trimble](https://www.tekla.com/). Проект не аффилирован с Trimble и не одобрен ею.

---

## Лицензия

[MIT](LICENSE).
