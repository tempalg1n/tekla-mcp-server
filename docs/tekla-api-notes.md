# Заметки по Tekla Open API + чек-лист проверки

Здесь собрано то, что известно про Tekla Open API на момент создания каркаса, и
**что обязательно проверить на Windows-машине**. Когда проверишь/поправишь пункт —
отметь его и впиши результат, чтобы следующий агент не копал заново.

Официальная справка: https://developer.tekla.com/doc/tekla-structures/2026/tekla-structures-64304

## Факты про Tekla 2026 (подтверждено в докладах/релиз-нотах)

- API-сборки таргетят **.NET Framework 4.8 / .NET Standard 2.0**.
- Поддерживаются **только x64** расширения (новое в 2026).
- **COM-поддержка убрана** из Open API сборок; сборки **больше не регистрируются в GAC**.
- Идёт постепенный переход с .NET Framework на современный .NET (начат в 2024) — в
  будущем, возможно, заработает и под `net8.0`. Пока ориентируемся на `net48`.
- Есть **NuGet-пакеты**: `Tekla.Structures`, `Tekla.Structures.Model`,
  `Tekla.Structures.Plugins` и др., версии вида `2026.0.x`.

## Модель подключения (важно)

Сервер — **standalone-процесс**, который подключается к **уже запущенной** Tekla с
открытой моделью. Связь устанавливает `new Tekla.Structures.Model.Model()`, статус —
`model.GetConnectionStatus()`. Это НЕ плагин внутри Tekla (плагин — другой сценарий,
`Tekla.Structures.Plugins`).

## Что использовано в `TeklaModelService.cs` — и что проверено

Проверено на Windows + Tekla 2023 (см. журнал ниже):

- [x] `new TSM.Model()` + `model.GetConnectionStatus()` → bool — базовое подключение.
- [x] `model.GetInfo()` → `ModelInfo`; поля `.ModelName`, `.ModelPath`.
      Проверить точные имена полей.
- [x] `model.GetModelObjectSelector().GetAllObjects()` → `ModelObjectEnumerator`;
      перебор через `while (en.MoveNext()) { var mo = en.Current; }`.
- [x] Каст `mo is TSM.Part part` для деталей. У `Part`: `.Name`, `.Class` (string),
      `.Profile.ProfileString`, `.Material.MaterialString`, `.Finish`.
- [x] `part.Identifier.ID` (int) и `part.Identifier.GUID` (System.Guid).
- [x] Report-свойства: `part.GetReportProperty("WEIGHT", ref double)`,
      `"LENGTH"` (ref double), `"ASSEMBLY_POS"` (ref string).
      **Проверить имена свойств и единицы** (вес — кг? длина — мм?).
- [x] Поиск по GUID: `new Tekla.Structures.Identifier(guid)` +
      `model.SelectModelObject(identifier)`. Уточнить сигнатуру/наличие метода.
- [x] Выделение в UI: `new Tekla.Structures.Model.UI.ModelObjectSelector()` +
      `.GetSelectedObjects()` → `ModelObjectEnumerator`.
- [x] Программное выделение в UI: `ModelObjectSelector.Select(ArrayList)` (добавлен `tekla_select_objects`).
- [ ] UDA-чтение: `GetUserProperty(name, ref string/int/double)` на нужных UDA вашего шаблона.
- [ ] UDA-запись: `SetUserProperty(name, value)` + `Modify()` на объектах нужных типов.

### Полезные report-свойства (для будущих инструментов)
`WEIGHT`, `WEIGHT_NET`, `LENGTH`, `HEIGHT`, `WIDTH`, `AREA`, `VOLUME`,
`PROFILE`, `MATERIAL`, `CLASS`, `NAME`, `ASSEMBLY_POS`, `PART_POS`, `PHASE`.
Полный список — в справке Tekla (раздел Template/Report properties).

## Сборка под Windows: окружение

- Windows x64; **Tekla Structures** установлена и **открыта с моделью**.
- **.NET SDK 8+** и **.NET Framework 4.8 Developer Pack** (targeting pack).
- Версии NuGet `Tekla.Structures.*` должны совпадать с установленной Tekla
  (сейчас в csproj стоит `2023.0.0` под проверенное окружение). Альтернатива NuGet —
  локальные `<Reference>` с `<HintPath>` на `...\Tekla Structures\...\bin\*.dll`.

## Риск: MCP SDK на .NET Framework 4.8

MCP C# SDK официально таргетит `netstandard2.0` + `net8.0`. Через `netstandard2.0` он
**должен** подключаться к `net48`, но возможны конфликты транзитивных зависимостей
(`System.Text.Json`, `Microsoft.Extensions.*`) и потребность в binding redirects
(в csproj уже включены `AutoGenerateBindingRedirects`).

- [x] Проверить, что `net48`-сборка сервера реально стартует и отвечает по stdio.
- Если не взлетит — переходим на **план B** (отдельный `net48`-воркер + `net8.0`-сервер),
  см. [architecture.md](architecture.md#запасной-план-b-два-процесса).

## Журнал проверок (заполнять при работе на Windows)

| Дата | Кто/агент | Что проверили | Результат / правка |
|------|-----------|---------------|--------------------|
| 2026-06-19 | Agent | `net48` сборка + MCP вызовы (`tools/list`, `tekla_get_connection_info`, `tekla_get_model_summary`, `tekla_find_objects`, `tekla_get_object_by_guid`, `tekla_get_selected_objects`) | Работает на живой модели Tekla 2023. |
| 2026-06-19 | Agent | Совместимость пакетов Tekla | Переключено с `2026.0.3` на `2023.0.0` в `TeklaMcp.Tekla.csproj`. |
| 2026-06-19 | Agent | Программное выделение в UI Tekla | Добавлен `tekla_select_objects` через `ModelObjectSelector.Select(ArrayList)`. |
| 2026-06-19 | Agent | Добавлены UDA tools | `tekla_get_object_udas`, `tekla_set_object_udas`, `tekla_set_udas_by_filter` (с preview по умолчанию). |
