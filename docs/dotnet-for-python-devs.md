# C# / .NET для питониста: шпаргалка под этот проект

Короткий перевод привычных Python-понятий на экосистему C#/.NET — чтобы ты ориентировался
в этом репозитории и понимал, какие команды и шаги тебе нужны дальше. Это не учебник по
языку, а карта местности.

## Аналогии «Python → .NET»

| Python | .NET / C# | В этом проекте |
|---|---|---|
| интерпретатор CPython | рантайм .NET (`.NET 8`) или .NET Framework 4.8 | `net8.0` на Mac, `net48` на Windows |
| `pip` | `dotnet` CLI + NuGet | `dotnet restore` тянет пакеты |
| PyPI | nuget.org | `ModelContextProtocol`, `Tekla.Structures.*` |
| `requirements.txt` / `pyproject.toml` | файл `*.csproj` (`<PackageReference>`) | по одному на проект |
| виртуальное окружение | папки `bin/` и `obj/` на проект | в `.gitignore` |
| пакет/модуль | сборка (assembly) + namespace | `TeklaMcp.Core` и т.д. |
| монорепо из пакетов | «solution» (`.sln`) из «projects» (`.csproj`) | `TeklaMcp.sln` |
| `python main.py` | `dotnet run --project <папка>` | запуск сервера |
| `pytest` | `dotnet test` (xUnit/NUnit) | тестов пока нет |
| `mypy` (типы опционально) | типы обязательны, проверяет компилятор | строгая типизация |
| `None` | `null`; `string?` — «может быть null» | `Nullable` включён |
| `dict` / `list` | `Dictionary<,>` / `List<>` | в DTO |
| декоратор `@app.tool()` | атрибут `[McpServerTool]` | в `Tools/*.cs` |
| `abc.ABC` / `Protocol` | `interface` | `ITeklaModelService` |

## Что такое «таргет-фреймворк» (TFM) и почему их два

В Python один интерпретатор. В .NET код компилируется под конкретный «таргет»:
- **`net8.0`** — современный кросс-платформенный .NET (Mac/Linux/Windows). На нём
  работает мок-версия на твоём Mac.
- **`net48`** — старый .NET Framework, только Windows. На нём работает Tekla.
- **`netstandard2.0`** — «общий контракт», который понимают оба. На нём `Core` и `Mock`,
  чтобы переиспользоваться в обеих сборках.

`<TargetFramework>` (один) или `<TargetFrameworks>` (несколько через `;`) задаются в
`.csproj`. Выбрать конкретный при запуске: `-f net48`.

## Минимальный набор команд

```bash
dotnet --info                              # что за SDK установлен
dotnet restore                             # скачать пакеты (аналог pip install)
dotnet build src/TeklaMcp.Server           # собрать
dotnet run   --project src/TeklaMcp.Server # запустить
dotnet build TeklaMcp.Mac.slnf             # собрать всё, что собирается на Mac
```

Условные обозначения в `.csproj`: `Condition="..."` — это «если», по сути препроцессор
MSBuild. В коде `#if NET48 ... #else ... #endif` — компиляция разных веток под разные TFM
(аналога в Python нет; ближе всего — `if sys.platform`, но здесь это на этапе компиляции).

## Твои дальнейшие шаги (рекомендации)

1. **Поставь .NET SDK на Mac** и запусти мок-сервер — убедись, что MCP-обвязка живая:
   ```bash
   brew install --cask dotnet-sdk
   dotnet run --project src/TeklaMcp.Server
   ```
   Можно потыкать инструменты через MCP Inspector (см. README).
2. **На рабочем Windows-компе** поставь .NET SDK 8 + .NET Framework 4.8 Developer Pack,
   открой Tekla с моделью и собери `net48`-версию. Пройдись по чек-листу
   [tekla-api-notes.md](tekla-api-notes.md) — почти наверняка пара вызовов API
   потребует мелких правок.
3. **Расширяй через интерфейс**: новый метод в `ITeklaModelService` → реализация в Mock и
   в Tekla → новый `[McpServerTool]`. Так мок-разработка на Mac остаётся возможной.

## Инструменты, которые стоит поставить

- **VS Code** + расширение **C# Dev Kit** (бесплатно, есть на Mac) — подсветка,
  навигация, отладка. Или **Rider** (JetBrains) — если привычен PyCharm, это его брат.
- На Windows исторически удобна **Visual Studio** (Community бесплатна) — открывает `.sln`.

## На что обратить внимание (грабли питониста в C#)

- **Точка с запятой** в конце операторов; фигурные скобки вместо отступов (отступы не
  значимы, но мы их соблюдаем).
- **Всё статически типизировано**: тип переменной известен на компиляции. `var` — это не
  «динамика», а вывод типа (как `auto`), тип всё равно фиксирован.
- **Свойства** `public string Name { get; set; }` — это «поля с геттером/сеттером», читаются
  как обычные атрибуты: `obj.Name`.
- **`async/await`** очень похожи на питоновские; здесь мы пока почти везде синхронны.
- Пакеты **не** ставятся глобально «в окружение» — зависимости описаны в `.csproj` и
  восстанавливаются в `obj/`/`bin/` каждого проекта.
