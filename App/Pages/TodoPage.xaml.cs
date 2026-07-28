using App.Models;
using App.Services;
using Cronos;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace App.Pages
{
    public sealed partial class TodoPage : Page
    {
        private readonly TodoRepository _repository = new();
        private readonly TodoSearchService _search = new();
        private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
        private Action? _cancelOperation;
        private string? _requestedTodoId;
        private bool _isCreatingGroup;
        private string? _creatingTodoCategory;

        public TodoPage()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                Focus(FocusState.Programmatic);
                Render();
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _requestedTodoId = e.Parameter as string;
            base.OnNavigatedTo(e);
        }

        private void Render()
        {
            _cancelOperation = null;
            TodoGroupsHost.Children.Clear();
            var todos = _repository.List();
            var categoryDocuments = _repository.ListCategories();
            var categoryDescriptions = categoryDocuments.ToDictionary(category => category.Id, category => category.Description, StringComparer.Ordinal);
            var categories = categoryDocuments
                .Select(category => category.Id)
                .Concat(todos.Select(todo => todo.Category))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(category => todos.Where(todo => todo.Category == category)
                    .Select(todo => todo.CreatedAt).DefaultIfEmpty(DateTime.MaxValue).Min())
                .ToList();

            var urgent = todos.Where(todo => !todo.IsDone && IsUrgent(todo, out _)).ToList();
            if (urgent.Count > 0) TodoGroupsHost.Children.Add(CreateUrgentSection(urgent));
            if (_isCreatingGroup) TodoGroupsHost.Children.Add(CreateNewGroupEditor());
            foreach (var category in categories)
            {
                categoryDescriptions.TryGetValue(category, out var description);
                TodoGroupsHost.Children.Add(CreateGroup(category, description ?? string.Empty, todos.Where(todo => todo.Category == category).ToList()));
            }

            EmptyState.Visibility = categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private UIElement CreateNewGroupEditor()
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var input = new TextBox
            {
                PlaceholderText = "New todo group",
                VerticalAlignment = VerticalAlignment.Center
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var save = IconButton("\uE74E", "Create group");
            var cancel = IconButton("\uE7A7", "Cancel");
            actions.Children.Add(save);
            actions.Children.Add(cancel);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(input);
            grid.Children.Add(actions);
            card.Child = grid;
            card.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
            void Cancel()
            {
                _isCreatingGroup = false;
                Render();
            }
            void Save()
            {
                if (string.IsNullOrWhiteSpace(input.Text)) return;
                _repository.SetCategoryDescription(input.Text, string.Empty);
                _isCreatingGroup = false;
                Render();
            }
            _cancelOperation = Cancel;
            cancel.Click += (_, _) => Cancel();
            save.Click += (_, _) => Save();
            input.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                Save();
            };
            return card;
        }

        private UIElement CreateUrgentSection(IReadOnlyList<TodoDocument> todos)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Due within an hour",
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.Red)
            });
            panel.Children.Add(CreateList(todos, true));
            return panel;
        }

        private UIElement CreateGroup(string category, string description, IReadOnlyList<TodoDocument> todos)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16)
            };
            var panel = new StackPanel { Spacing = 10 };
            card.Child = panel;
            panel.Children.Add(CreateGroupHeader(category, description, todos.Count));

            var showDone = _expandedGroups.Contains(category);
            var visible = todos
                .Where(todo => showDone || !todo.IsDone)
                .OrderByDescending(todo => !string.IsNullOrWhiteSpace(todo.Notify))
                .ThenByDescending(todo => todo.CreatedAt)
                .ToList();
            if (visible.Count > 0)
                panel.Children.Add(CreateList(visible, false));

            if (string.Equals(_creatingTodoCategory, category, StringComparison.Ordinal))
                panel.Children.Add(CreateNewTodoEditor(category));
            else
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var newTodo = new HyperlinkButton
                {
                    Content = "New Todo",
                    Tag = category
                };
                newTodo.Click += NewTodoButton_Click;
                actions.Children.Add(newTodo);
                if (todos.Any(todo => todo.IsDone))
                {
                    var toggle = new HyperlinkButton
                    {
                        Content = showDone ? "Hide done" : $"Show done ({todos.Count(todo => todo.IsDone)})"
                    };
                    toggle.Click += (_, _) =>
                    {
                        if (!_expandedGroups.Add(category)) _expandedGroups.Remove(category);
                        Render();
                    };
                    actions.Children.Add(toggle);
                }
                panel.Children.Add(actions);
            }
            return card;
        }

        private UIElement CreateNewTodoEditor(string category)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var input = new TextBox
            {
                PlaceholderText = "New todo",
                VerticalAlignment = VerticalAlignment.Center
            };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var save = IconButton("\uE74E", "Create todo");
            var cancel = IconButton("\uE7A7", "Cancel");
            actions.Children.Add(save);
            actions.Children.Add(cancel);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(input);
            grid.Children.Add(actions);
            grid.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
            void Cancel()
            {
                _creatingTodoCategory = null;
                Render();
            }
            async void Save()
            {
                if (string.IsNullOrWhiteSpace(input.Text)) return;
                var todo = _repository.Create(input.Text, category, "user");
                _creatingTodoCategory = null;
                Render();
                try { await _search.RefreshEmbeddingAsync(todo); }
                catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Todo embedding failed: {exception.Message}"); }
            }
            _cancelOperation = Cancel;
            cancel.Click += (_, _) => Cancel();
            save.Click += (_, _) => Save();
            input.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                Save();
            };
            return grid;
        }

        private UIElement CreateGroupHeader(string category, string description, int todoCount)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleHost = new StackPanel { Spacing = 3 };
            titleHost.Children.Add(new TextBlock
            {
                Text = category,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrWhiteSpace(description))
            {
                titleHost.Children.Add(new TextBlock
                {
                    Text = description,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
            var edit = IconButton("\uE70F", "Rename group");
            edit.Opacity = 0;
            edit.IsHitTestVisible = false;
            Grid.SetColumn(edit, 1);
            grid.Children.Add(titleHost);
            grid.Children.Add(edit);
            void SetEditVisibility(bool visible)
            {
                edit.Opacity = visible ? 1 : 0;
                edit.IsHitTestVisible = visible;
            }
            grid.PointerEntered += (_, _) => SetEditVisibility(true);
            grid.PointerExited += (_, _) => SetEditVisibility(false);
            grid.GotFocus += (_, _) => SetEditVisibility(true);
            grid.LostFocus += (_, _) => SetEditVisibility(false);
            edit.Click += (_, _) =>
            {
                var input = new TextBox { Text = category, VerticalAlignment = VerticalAlignment.Center };
                var descriptionInput = new TextBox
                {
                    Text = description,
                    PlaceholderText = "Description",
                    FontSize = 11,
                    MinHeight = 0,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var inputs = new StackPanel();
                inputs.Children.Add(input);
                inputs.Children.Add(descriptionInput);
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var save = IconButton("\uE74E", "Save");
                var cancel = IconButton("\uE7A7", "Cancel");
                actions.Children.Add(save);
                actions.Children.Add(cancel);
                Grid.SetColumn(actions, 1);
                grid.Children.Clear();
                grid.Children.Add(inputs);
                grid.Children.Add(actions);
                input.SelectAll();
                input.Focus(FocusState.Programmatic);
                void Cancel() => Render();
                async void Save()
                {
                    if (string.IsNullOrWhiteSpace(input.Text))
                    {
                        if (todoCount > 0)
                        {
                            var dialog = new ContentDialog
                            {
                                XamlRoot = XamlRoot,
                                Title = "Delete group?",
                                Content = new TextBlock
                                {
                                    Text = $"This group contains {todoCount} todo{(todoCount == 1 ? string.Empty : "s")}. Deleting it will also delete all of them.",
                                    TextWrapping = TextWrapping.Wrap
                                },
                                PrimaryButtonText = "Delete group",
                                CloseButtonText = "Cancel",
                                DefaultButton = ContentDialogButton.Close
                            };
                            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                        }
                        _repository.DeleteCategory(category);
                    }
                    else
                    {
                        _repository.RenameCategory(category, input.Text);
                        _repository.SetCategoryDescription(input.Text, descriptionInput.Text);
                    }
                    Render();
                }
                _cancelOperation = Cancel;
                cancel.Click += (_, _) => Cancel();
                save.Click += (_, _) => Save();
                input.KeyDown += (_, e) =>
                {
                    if (e.Key != VirtualKey.Enter) return;
                    e.Handled = true;
                    Save();
                };
                descriptionInput.KeyDown += (_, e) =>
                {
                    if (e.Key != VirtualKey.Enter) return;
                    e.Handled = true;
                    Save();
                };
            };
            return grid;
        }

        private ListView CreateList(IReadOnlyList<TodoDocument> todos, bool urgent)
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            foreach (var todo in todos)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = CreateTodoItem(todo, urgent),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }
            return list;
        }

        private UIElement CreateTodoItem(TodoDocument todo, bool urgent)
        {
            var root = new Grid { Padding = new Thickness(4, 8, 4, 8), MinHeight = 58 };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkBox = new CheckBox
            {
                IsChecked = todo.IsDone,
                Width = 20,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 0)
            };
            checkBox.Click += (_, _) =>
            {
                _repository.SetDone(todo.Id, checkBox.IsChecked == true);
                Render();
            };
            root.Children.Add(checkBox);

            var text = new StackPanel { Spacing = 3 };
            Grid.SetColumn(text, 1);
            var title = new TextBlock
            {
                Text = todo.Value,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                Foreground = urgent
                    ? new SolidColorBrush(Colors.Red)
                    : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            };
            if (todo.IsDone) title.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
            text.Children.Add(title);
            text.Children.Add(new TextBlock
            {
                Text = TodoDetails(todo),
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(text);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Opacity = 0,
                IsHitTestVisible = false
            };
            Grid.SetColumn(actions, 2);
            var notify = IconButton("\uEE93", "Set notification");
            var edit = IconButton("\uE70F", "Edit todo");
            var delete = IconButton("\uE74D", "Delete todo");
            actions.Children.Add(notify);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            root.Children.Add(actions);
            void SetActionsVisible(bool visible)
            {
                actions.Opacity = visible ? 1 : 0;
                actions.IsHitTestVisible = visible;
            }
            root.PointerEntered += (_, _) => SetActionsVisible(true);
            root.PointerExited += (_, _) => { if (_cancelOperation is null) SetActionsVisible(false); };

            notify.Click += async (_, _) => await ConfigureNotificationAsync(todo);
            edit.Click += (_, _) => BeginTodoEdit(root, todo);
            delete.Click += (_, _) => BeginDelete(actions, todo);
            if (todo.Id == _requestedTodoId)
            {
                _requestedTodoId = null;
                root.Background = (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"];
                root.Loaded += (_, _) => root.StartBringIntoView();
            }
            return root;
        }

        private async Task ConfigureNotificationAsync(TodoDocument todo)
        {
            var now = DateTimeOffset.Now;
            var date = new CalendarDatePicker
            {
                Header = "Date",
                Date = now,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var time = new TimePicker
            {
                Header = "Time",
                Time = now.TimeOfDay,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var preset = new ComboBox
            {
                Header = "Repeat",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };
            preset.Items.Add(new ComboBoxItem { Content = "Selected date" });
            preset.Items.Add(new ComboBoxItem { Content = "Every hour" });
            preset.Items.Add(new ComboBoxItem { Content = "Every day" });
            preset.Items.Add(new ComboBoxItem { Content = "Tomorrow" });
            preset.Items.Add(new ComboBoxItem { Content = "Every morning" });
            preset.Items.Add(new ComboBoxItem { Content = "Every evening" });
            preset.Items.Add(new ComboBoxItem { Content = "Every week" });
            preset.Items.Add(new ComboBoxItem { Content = "Every month" });
            preset.Items.Add(new ComboBoxItem { Content = "Every year" });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Notify: {todo.Value}",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children = { preset, date, time }
                },
                PrimaryButtonText = "Save notification",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var selectedDate = date.Date?.LocalDateTime ?? DateTime.Now;
            todo.Notify = NotificationSchedule(preset.SelectedIndex, selectedDate, time.Time);
            todo.NotifiedAt = DateTime.UtcNow;
            _repository.Update(todo);
            Render();
        }

        private static string NotificationSchedule(int preset, DateTime date, TimeSpan time)
        {
            var minute = time.Minutes;
            var hour = time.Hours;
            return preset switch
            {
                1 => $"{minute} * * * *",
                2 => $"{minute} {hour} * * *",
                3 => $"{minute} {hour} {DateTime.Today.AddDays(1).Day} {DateTime.Today.AddDays(1).Month} *",
                4 => "0 9 * * *",
                5 => "0 18 * * *",
                6 => $"{minute} {hour} * * {(int)date.DayOfWeek}",
                7 => $"{minute} {hour} {date.Day} * *",
                _ => $"{minute} {hour} {date.Day} {date.Month} *"
            };
        }

        private void BeginTodoEdit(Grid root, TodoDocument todo)
        {
            var input = new TextBox { Text = todo.Value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(input, 1);
            var actions = (StackPanel)root.Children[2];
            actions.Children.Clear();
            var save = IconButton("\uE74E", "Save");
            var cancel = IconButton("\uE7A7", "Cancel");
            actions.Children.Add(save);
            actions.Children.Add(cancel);
            root.Children.RemoveAt(1);
            root.Children.Insert(1, input);
            input.SelectAll();
            input.Focus(FocusState.Programmatic);
            void Cancel() => Render();
            async void Save()
            {
                if (string.IsNullOrWhiteSpace(input.Text))
                {
                    _repository.Delete(todo.Id);
                    Render();
                    return;
                }

                var value = input.Text.Trim();
                if (string.Equals(todo.Value, value, StringComparison.Ordinal))
                {
                    Render();
                    return;
                }

                todo.Value = value;
                todo.Embedding = [];
                _repository.Update(todo);
                Render();
                try { await _search.RefreshEmbeddingAsync(todo); }
                catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Todo embedding failed: {exception.Message}"); }
            }
            _cancelOperation = Cancel;
            cancel.Click += (_, _) => Cancel();
            save.Click += (_, _) => Save();
            input.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                Save();
            };
        }

        private void BeginDelete(StackPanel actions, TodoDocument todo)
        {
            actions.Children.Clear();
            var accept = IconButton("\uE8FB", "Confirm delete");
            var cancel = IconButton("\uE7A7", "Cancel");
            actions.Children.Add(accept);
            actions.Children.Add(cancel);
            void Cancel() => Render();
            _cancelOperation = Cancel;
            cancel.Click += (_, _) => Cancel();
            accept.Click += (_, _) =>
            {
                _repository.Delete(todo.Id);
                Render();
            };
        }

        private static Button IconButton(string glyph, string toolTip)
        {
            var button = new Button { Content = new FontIcon { Glyph = glyph }, Padding = new Thickness(8) };
            ToolTipService.SetToolTip(button, toolTip);
            return button;
        }

        private static string TodoDetails(TodoDocument todo)
        {
            if (!string.IsNullOrWhiteSpace(todo.Notify) && TryNextSchedule(todo.Notify, out var next))
                return $"{RelativeSchedule(next)} · {ScheduleRecurrence(todo.Notify)}";

            var details = todo.CreatedBy == "secretary" ? "Added by Assistant" : CreatedDescription(todo.CreatedAt);
            if (!string.IsNullOrWhiteSpace(todo.Notify)) details += " · Schedule unavailable";
            return details;
        }

        private static string RelativeSchedule(DateTimeOffset occurrence)
        {
            var remaining = occurrence - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) return "Due now";
            if (remaining < TimeSpan.FromMinutes(60)) return $"Due in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min";
            if (occurrence.Date == DateTimeOffset.Now.Date) return $"Due at {occurrence:t}";
            if (occurrence.Date == DateTimeOffset.Now.AddDays(1).Date) return $"Due tomorrow at {occurrence:t}";
            return $"Due {occurrence:MMM d, h:mm tt}";
        }

        private static string ScheduleRecurrence(string expression)
        {
            var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5) return "Scheduled";
            if (fields[1] == "*") return "Repeats hourly";
            if (fields[2] == "*" && fields[3] == "*" && fields[4] == "*")
            {
                if (fields[0] == "0" && fields[1] == "9") return "Every morning";
                if (fields[0] == "0" && fields[1] == "18") return "Every evening";
                return "Repeats daily";
            }
            if (fields[2] == "*" && fields[3] == "*") return "Repeats weekly";
            if (fields[3] == "*") return "Repeats monthly";
            return "Repeats yearly";
        }

        private static string CreatedDescription(DateTime createdAt)
        {
            var localCreated = createdAt.ToLocalTime().Date;
            var age = Math.Max(0, (DateTime.Now.Date - localCreated).Days);
            return age switch
            {
                0 => "Created today",
                1 => "Created yesterday",
                < 7 => $"Created {age} days ago",
                _ => $"Created {localCreated:MMM d}"
            };
        }

        private static bool IsUrgent(TodoDocument todo, out DateTimeOffset occurrence)
        {
            occurrence = default;
            if (string.IsNullOrWhiteSpace(todo.Notify) || !TryNextSchedule(todo.Notify, out occurrence)) return false;
            var now = DateTimeOffset.Now;
            return occurrence >= now.AddHours(-1)
                && occurrence <= now.AddHours(1)
                && occurrence.UtcDateTime > todo.NotifiedAt.ToUniversalTime();
        }

        private static bool TryNextSchedule(string expression, out DateTimeOffset occurrence)
        {
            occurrence = default;
            try
            {
                var now = DateTimeOffset.Now;
                var cron = CronExpression.Parse(expression, CronFormat.Standard);
                var value = cron.GetNextOccurrence(now.AddHours(-1).AddTicks(-1), TimeZoneInfo.Local);
                if (value is null) return false;
                occurrence = value.Value;
                return true;
            }
            catch (CronFormatException) { return false; }
        }

        private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Escape || _cancelOperation is null) return;
            e.Handled = true;
            _cancelOperation();
        }

        private void CreateGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCreatingGroup) return;
            _isCreatingGroup = true;
            Render();
        }

        private void NewTodoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not HyperlinkButton { Tag: string category }) return;
            _creatingTodoCategory = category;
            Render();
        }
    }
}
