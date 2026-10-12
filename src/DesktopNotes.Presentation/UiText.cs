using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using DesktopNotes.Application;

namespace DesktopNotes.Presentation;

/// <summary>UI-only strings: changing language never rewrites saved note content.</summary>
public sealed class UiText : INotifyPropertyChanged
{
    public static UiText Current { get; } = new();
    private AppLanguage _language;
    public event PropertyChangedEventHandler? PropertyChanged;
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["Настройки"] = "Settings",
        ["Интерфейс"] = "Interface",
        ["Язык приложения"] = "App language",
        ["Изменения применяются сразу и сохраняются автоматически."] = "Changes apply immediately and are saved automatically.",
        ["Логирование"] = "Logging",
        ["Записывать журнал"] = "Enable logging",
        ["Помогает разобраться в ошибках приложения."] = "Helps diagnose application errors.",
        ["Уровень логирования"] = "Log level",
        ["Записываются события выбранного уровня и более важные."] = "Records events at the selected level and above.",
        ["Информация"] = "Information",
        ["Предупреждения"] = "Warnings",
        ["Ошибки"] = "Errors",
        ["Интеграция с Windows"] = "Windows integration",
        ["Меню рабочего стола"] = "Desktop context menu",
        ["В Windows 11 пункт доступен через «Показать дополнительные параметры»."] = "In Windows 11, use “Show more options” to access this command.",
        ["Пункт меню установлен."] = "The menu command is installed.",
        ["Пункт меню не установлен."] = "The menu command is not installed.",
        ["Добавить пункт меню"] = "Install command",
        ["Удалить пункт меню"] = "Remove command",
        ["Данные"] = "Data",
        ["Заметки хранятся локально. Автозапуск выключен."] = "Notes are stored locally. Startup at sign-in is disabled.",
        ["Открыть папку заметок"] = "Open notes folder",
        ["Открыть папку журналов"] = "Open logs folder",
        ["Закрыть"] = "Close",
        ["Не удалось сохранить настройки: "] = "Could not save settings: ",
        ["Не удалось изменить меню: "] = "Could not update the menu: ",
        ["Не удалось открыть папку: "] = "Could not open the folder: ",
        ["Создать заметку"] = "New note",
        ["Показать все"] = "Show all notes",
        ["Повторить сохранение"] = "Retry saving",
        ["Выход"] = "Exit",
        ["Удаление заметки"] = "Delete note",
        ["Удалить заметку?"] = "Delete this note?",
        ["Заметка и её связи будут удалены без возможности восстановления."] = "The note and its connections will be permanently deleted.",
        ["Отмена"] = "Cancel",
        ["Удалить"] = "Delete",
        ["Оформление текста"] = "Text formatting",
        ["Жирный — Ctrl+B"] = "Bold — Ctrl+B",
        ["Курсив — Ctrl+I"] = "Italic — Ctrl+I",
        ["Код — Ctrl+`"] = "Code — Ctrl+`",
        ["Подчёркнутый — Ctrl+U"] = "Underline — Ctrl+U",
        ["Блок кода"] = "Code block",
        ["Зачёркнутый"] = "Strikethrough",
        ["Выделить маркером"] = "Highlight",
        ["Заголовок"] = "Heading",
        ["Обычный текст"] = "Normal text",
        ["Задача — Ctrl+L"] = "Task — Ctrl+L",
        ["Таблица — Ctrl+Shift+T"] = "Table — Ctrl+Shift+T",
        ["Скрыть"] = "Hide",
        ["Цвет рамки группы…"] = "Group border color…",
        ["Распустить группу"] = "Ungroup",
        ["Группа не двигается: участник закреплён"] = "Group is locked: a note is pinned",
        ["Цвет и тема заметки"] = "Note color and theme",
        ["Размер шрифта"] = "Font size",
        ["Увеличить размер шрифта"] = "Increase font size",
        ["Уменьшить размер шрифта"] = "Decrease font size",
        ["Жирный шрифт"] = "Bold",
        ["Выравнивание текста"] = "Text alignment",
        ["Закрепить положение заметки"] = "Pin note position",
        ["Открепить положение заметки"] = "Unpin note position",
        ["Расположение окон"] = "Window layer",
        ["Удалить заметку"] = "Delete note",
        ["По левому краю"] = "Align left",
        ["По центру"] = "Center",
        ["По правому краю"] = "Align right",
        ["По ширине"] = "Justify",
        ["Сверху"] = "Top",
        ["По середине"] = "Middle",
        ["Снизу"] = "Bottom",
        ["Обычный"] = "Normal",
        ["Поверх окон"] = "Always on top",
        ["На задний план"] = "Send to back",
        ["Свернуть / развернуть"] = "Collapse / expand",
        ["Текст заметки — Markdown Live Preview"] = "Note text — Markdown Live Preview",
        ["Учитывать регистр"] = "Match case",
        ["Слова целиком"] = "Whole words",
        ["Регулярное выражение"] = "Regular expression",
        ["Следующее совпадение (F3)"] = "Next match (F3)",
        ["Предыдущее совпадение (Shift+F3)"] = "Previous match (Shift+F3)",
        ["Ошибка: "] = "Error: ",
        ["Совпадений нет"] = "No matches found",
        ["Добавить строку"] = "Add row",
        ["Добавить столбец"] = "Add column",
        ["Задача выполнена"] = "Task completed",
        ["[изображение]"] = "[image]",
        ["Не удалось открыть ссылку: "] = "Could not open the link: ",
        ["Не удалось изменить порядок окон: "] = "Could not change the window layer: ",
        ["Заметка не найдена: "] = "Note not found: ",
        ["Не удалось отслеживать завершение выбора вне стикеров. Кликните по стикеру без Ctrl для завершения.\n"] = "Could not detect clicks outside notes. Click a note without Ctrl to finish selection.\n",
        ["Новая заметка"] = "New note",
        ["ссылка"] = "link",
        ["| Заголовок 1 | Заголовок 2 |\n| --- | --- |\n|  |  |\n"] = "| Heading 1 | Heading 2 |\n| --- | --- |\n|  |  |\n",
        ["Не удалось сохранить заметки"] = "Could not save notes",
        ["Изменения остаются в памяти. В меню трея доступно повторное сохранение. "] = "Changes remain in memory. Use the tray menu to retry saving. ",
        ["Журнал: "] = "Logs: ",
        ["DesktopNotes — восстановление"] = "DesktopNotes — recovery",
        ["DesktopNotes — группы"] = "DesktopNotes — groups",
        ["DesktopNotes завершилось с ошибкой.\n\n"] = "DesktopNotes stopped due to an error.\n\n",
        ["Не удалось запустить DesktopNotes.\n\n"] = "Could not start DesktopNotes.\n\n",
        ["Выход отменён: изменения не сохранены. Освободите место или восстановите доступ к папке, затем повторите выход."] = "Exit cancelled: changes could not be saved. Free up disk space or restore folder access, then try again.",
        ["Завершение сеанса отменено: заметки не удалось сохранить. Повторите сохранение через меню трея."] = "Sign-out cancelled: notes could not be saved. Retry saving from the tray menu.",
        ["Ошибка отображения Markdown. Заметки доступны в исходном виде; текст сохранён без преобразования.\n\nЖурнал: "] = "Markdown rendering failed. Notes are available as source text; their content was preserved.\n\nLogs: ",
        ["DesktopNotes завершится из-за ошибки.\n\n"] = "DesktopNotes will close due to an error.\n\n",
        ["\n\nЖурнал: "] = "\n\nLogs: ",
        ["Основной файл повреждён или недоступен. Заметки восстановлены из резервной копии."] = "The primary file is damaged or unavailable. Notes were restored from the backup.",
        ["Эта заметка уже состоит в другой группе. Сначала исключите её из той группы."] = "This note already belongs to another group. Remove it from that group first.",
    };
    public string this[string key] => _language == AppLanguage.English && English.TryGetValue(key, out var text) ? text : key;
    public static string Get(string key) => Current[key];
    public static void SetLanguage(AppLanguage language)
    {
        if (Current._language == language) return;
        Current._language = language;
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(null));
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding { Source = UiText.Current, Path = new System.Windows.PropertyPath("[" + key + "]"), Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
