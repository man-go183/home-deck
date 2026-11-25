using System.Collections.ObjectModel;
using HomeDeck.Services;
using Microsoft.Maui.ApplicationModel;

namespace HomeDeck.Views;

/// <summary>Native app-pick sheet (multi-select + search). The human sees
/// names; the caller gets back only picked IDs. Null = cancelled.
/// Shown modally; every path completes the task (fail-closed).</summary>
public sealed class AppPickPage : ContentPage
{
    readonly TaskCompletionSource<List<HdFileGuard.AppRef>?> _tcs = new();
    readonly List<HdFileGuard.AppRef> _all;
    readonly ObservableCollection<Row> _rows = new();
    readonly HashSet<string> _checked;
    readonly Label _count;
    bool _done;

    sealed class Row
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Picked { get; set; }
    }

    AppPickPage(List<HdFileGuard.AppRef> apps, HashSet<string> preselected)
    {
        _all = apps;
        _checked = preselected;
        Title = "Let this site open…";

        var title = new Label { Text = "Choose which apps this site may open:", Margin = new Thickness(16, 16, 16, 4) };
        var search = new SearchBar { Placeholder = "Search apps", Margin = new Thickness(12, 0, 12, 0) };
        search.TextChanged += (_, e) => Rebuild(e.NewTextValue);

        var list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            Margin = new Thickness(12, 4, 12, 4),
            ItemTemplate = new DataTemplate(() =>
            {
                var box = new CheckBox { VerticalOptions = LayoutOptions.Center };
                var name = new Label { VerticalOptions = LayoutOptions.Center };
                box.CheckedChanged += (s, _) =>
                {
                    try
                    {
                        if (s is CheckBox c && c.BindingContext is Row r)
                        {
                            r.Picked = c.IsChecked;
                            if (c.IsChecked) _checked.Add(r.Id);
                            else _checked.Remove(r.Id);
                            UpdateCount();
                        }
                    }
                    catch { }
                };
                name.SetBinding(Label.TextProperty, "Name");
                box.SetBinding(CheckBox.IsCheckedProperty, "Picked");
                return new HorizontalStackLayout
                {
                    Spacing = 12,
                    Padding = new Thickness(4, 6),
                    Children = { box, name }
                };
            })
        };
        list.ItemsSource = _rows;

        _count = new Label { Margin = new Thickness(16, 0, 16, 0) };
        var cancel = new Button { Text = "Cancel" };
        cancel.Clicked += (_, _) => Finish(null);
        var ok = new Button { Text = "Allow selected" };
        ok.Clicked += (_, _) => Finish(_all.Where(a => a != null && _checked.Contains(a.Id)).ToList());
        var bar = new HorizontalStackLayout
        {
            Spacing = 12,
            Margin = new Thickness(16, 8, 16, 16),
            Children = { cancel, ok }
        };

        var inner = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            Children = { title, search, list, _count, bar },
        };
        Grid.SetRow(title, 0);
        Grid.SetRow(search, 1);
        Grid.SetRow(list, 2);
        Grid.SetRow(_count, 3);
        Grid.SetRow(bar, 4);

        Color card = Color.FromArgb("#1E1E1E");
        try { card = (Color)Application.Current.Resources["HdCard"]; } catch { }
        var dialog = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            BackgroundColor = card,
            Stroke = Color.FromRgba(255, 255, 255, 40),
            StrokeThickness = 1,
            Padding = new Thickness(8),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 520,
            MaximumHeightRequest = 700,
            Content = inner,
        };
        BackgroundColor = Color.FromRgba(0, 0, 0, 150);
        Content = new Grid { Children = { dialog } };
        Rebuild(null);
    }

    void Rebuild(string? filter)
    {
        try
        {
            _rows.Clear();
            var f = (filter ?? "").Trim();
            foreach (var a in _all)
            {
                try
                {
                    if (a == null || string.IsNullOrEmpty(a.Id)) continue;
                    if (f.Length > 0 && !a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                        && !a.Id.Contains(f, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (_rows.Count >= 2000) break;
                    _rows.Add(new Row { Id = a.Id, Name = a.Name, Picked = _checked.Contains(a.Id) });
                }
                catch { }
            }
            UpdateCount();
        }
        catch { }
    }

    void UpdateCount()
    {
        try { _count.Text = _checked.Count == 0 ? "Nothing selected" : _checked.Count + " selected"; } catch { }
    }

    void Finish(List<HdFileGuard.AppRef>? picked)
    {
        try
        {
            if (_done) return;
            _done = true;
            _tcs.TrySetResult(picked);
            MainThread.BeginInvokeOnMainThread(() => { try { Navigation.PopModalAsync(); } catch { } });
        }
        catch { try { _tcs.TrySetResult(null); } catch { } }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try { if (!_done) { _done = true; _tcs.TrySetResult(null); } } catch { }
    }

    /// <summary>Show the sheet over nav and complete with the picked apps
    /// (possibly empty = user cleared the set) or null on cancel.</summary>
    public static Task<List<HdFileGuard.AppRef>?> PickAsync(INavigation nav, List<HdFileGuard.AppRef> apps, List<string> preselected)
    {
        var page = new AppPickPage(apps ?? new(), new HashSet<string>(preselected ?? new()));
        try { MainThread.BeginInvokeOnMainThread(() => { try { nav.PushModalAsync(page); } catch { page.Finish(null); } }); }
        catch { page.Finish(null); }
        return page._tcs.Task;
    }
}
