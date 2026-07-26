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
            var categories = _repository.ListCategories()
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
                TodoGroupsHost.Children.Add(CreateGroup(category, todos.Where(todo => todo.Category == category).ToList(), urgent));

            EmptyState.Visibility = todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            _cancelOperation = Cancel;
            cancel.Click += (_, _) => Cancel();
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(input.Text)) return;
                _repository.SetCategoryDescription(input.Text, string.Empty);
                _isCreatingGroup = false;
                Render();
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
                Foreground = new SolidColorBrush(Colors.Red)
            });
            panel.Children.Add(CreateList(todos, true));
            return panel;
        }

        private UIElement CreateGroup(string category, IReadOnlyList<TodoDocument> todos, IReadOnlyList<TodoDocument> urgent)
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
            panel.Children.Add(CreateGroupHeader(category));

            var showDone = _expandedGroups.Contains(category);
            var visible = todos
                .Where(todo => (showDone || !todo.IsDone) && !urgent.Any(item => item.Id == todo.Id))
                .OrderByDescending(todo => !string.IsNullOrWhiteSpace(todo.Notify))
                .ThenByDescending(todo => todo.CreatedAt)
                .ToList();
            if (visible.Count > 0)
                panel.Children.Add(CreateList(visible, false));
            else
                panel.Children.Add(new TextBlock
                {
                    Text = todos.Any(todo => todo.IsDone) ? "All todos are done." : "No todos in this group.",
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(12, 8, 12, 8)
                });

            if (todos.Any(todo => todo.IsDone))
            {
                var toggle = new Button
                {
                    Content = showDone ? "Hide done" : $"Show done ({todos.Count(todo => todo.IsDone)})",
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                toggle.Click += (_, _) =>
                {
                    if (!_expandedGroups.Add(category)) _expandedGroups.Remove(category);
                    Render();
                };
                panel.Children.Add(toggle);
            }
            return card;
        }

        private UIElement CreateGroupHeader(string category)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = category,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center
            };
            var edit = IconButton("\uE70F", "Rename group");
            Grid.SetColumn(edit, 1);
            grid.Children.Add(title);
            grid.Children.Add(edit);
            edit.Click += (_, _) =>
            {
                var input = new TextBox { Text = category, Header = "Group name" };
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var save = IconButton("\uE74E", "Save");
                var cancel = IconButton("\uE7A7", "Cancel");
                actions.Children.Add(save);
                actions.Children.Add(cancel);
                Grid.SetColumn(actions, 1);
                grid.Children.Clear();
                grid.Children.Add(input);
                grid.Children.Add(actions);
                input.SelectAll();
                input.Focus(FocusState.Programmatic);
                void Cancel() => Render();
                _cancelOperation = Cancel;
                cancel.Click += (_, _) => Cancel();
                save.Click += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(input.Text)) return;
                    _repository.RenameCategory(category, input.Text);
                    Render();
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
            foreach (var todo in todos) list.Items.Add(CreateTodoItem(todo, urgent));
            return list;
        }

        private UIElement CreateTodoItem(TodoDocument todo, bool urgent)
        {
            var root = new Grid { Padding = new Thickness(4, 8, 4, 8), MinHeight = 58 };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkBox = new CheckBox { IsChecked = todo.IsDone, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
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
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = urgent ? new SolidColorBrush(Colors.Red) : null
            };
            if (todo.IsDone) title.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
            text.Children.Add(title);
            text.Children.Add(new TextBlock
            {
                Text = TodoDetails(todo),
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(text);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Visibility = Visibility.Collapsed };
            Grid.SetColumn(actions, 2);
            var edit = IconButton("\uE70F", "Edit todo");
            var delete = IconButton("\uE74D", "Delete todo");
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            root.Children.Add(actions);
            root.PointerEntered += (_, _) => actions.Visibility = Visibility.Visible;
            root.PointerExited += (_, _) => { if (_cancelOperation is null) actions.Visibility = Visibility.Collapsed; };

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

        private void BeginTodoEdit(Grid root, TodoDocument todo)
        {
            var input = new TextBox { Text = todo.Value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
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
            _cancelOperation = Cancel;
            cancel.Click += (_, _) => Cancel();
            save.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(input.Text)) return;
                todo.Value = input.Text.Trim();
                todo.Embedding = [];
                _repository.Update(todo);
                Render();
                try { await _search.RefreshEmbeddingAsync(todo); }
                catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Todo embedding failed: {exception.Message}"); }
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
            var localCreated = todo.CreatedAt.ToLocalTime();
            var age = Math.Max(0, (DateTime.Now.Date - localCreated.Date).Days);
            var details = $"Created {localCreated:g} by {todo.CreatedBy} · {age} day{(age == 1 ? string.Empty : "s")} ago";
            if (!string.IsNullOrWhiteSpace(todo.Notify))
                details += TryNextSchedule(todo.Notify, out var next)
                    ? $" · Scheduled {next:g}"
                    : $" · Scheduled ({todo.Notify})";
            return details;
        }

        private static bool IsUrgent(TodoDocument todo, out DateTimeOffset occurrence)
        {
            occurrence = default;
            if (string.IsNullOrWhiteSpace(todo.Notify) || !TryNextSchedule(todo.Notify, out occurrence)) return false;
            var now = DateTimeOffset.Now;
            return occurrence >= now.AddHours(-1) && occurrence <= now.AddHours(1);
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
    }
}
