using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Enfilade;

namespace CtxUi.Extensions;

public sealed class PanelLayout
{
    private sealed class Entry(string id, Func<string> title, bool isCaption, Action<EnfiladeBuilder, Func<string>> add)
    {
        public string Id { get; } = id;

        public bool IsCaption { get; } = isCaption;

        public bool Hidden { get; set; }

        public Func<string> Title { get; set; } = title;

        public void AddTo(EnfiladeBuilder shell) => add(shell, Title);
    }

    private readonly List<Entry> _entries = [];
    private readonly List<string> _stylesheets = [];
    private readonly List<(Func<string> Title, string Href)> _accountLinks = [];
    private readonly HashSet<Assembly> _pages = [];
    private readonly PanelLook _look = new();

    internal PanelLayout()
    {
    }

    internal PanelSlotRegistry Slots { get; } = new();

    public IReadOnlyList<string> VisibleEntries => [.. Visible().Select(entry => entry.Id)];

    public PanelEditor EditorFor(string assemblyName) => new(this, assemblyName);

    public void Apply(EnfiladeBuilder shell)
    {
        foreach (var entry in Visible())
            entry.AddTo(shell);
        foreach (var assembly in _pages)
            shell.AddPages(assembly);
        foreach (var (title, href) in _accountLinks)
            shell.AddAccountLink(title, href);
        foreach (var stylesheet in _stylesheets)
            shell.AddStylesheet(stylesheet);
        shell.Services.PostConfigure<EnfiladeOptions>(ApplyLook);
        Slots.Freeze();
        shell.Services.AddSingleton(Slots);
    }

    internal PanelLayout AddBuiltIn<TPage>(string id, Func<string> title) where TPage : IComponent
    {
        _entries.Add(SectionEntry<TPage>(id, title, null));
        return this;
    }

    internal PanelLayout AddBuiltInCaption(string id, Func<string> title)
    {
        _entries.Add(CaptionEntry(id, title));
        return this;
    }

    internal PanelLayout AddBuiltInStylesheet(string path)
    {
        _stylesheets.Add(path);
        return this;
    }

    internal string InsertSection<TPage>(string id, Func<string> title, Action<SectionSetup>? setup, PanelPosition? at, string? previous)
        where TPage : IComponent
    {
        Insert(SectionEntry<TPage>(id, title, setup), at, previous);
        return id;
    }

    internal string InsertCaption(string id, Func<string> title, PanelPosition? at, string? previous)
    {
        Insert(CaptionEntry(id, title), at, previous);
        return id;
    }

    internal string TitleOf(string id) => Find(id).Title();

    internal void Hide(string id) => Find(id).Hidden = true;

    internal void Rename(string id, Func<string> title) => Find(id).Title = title;

    internal void Move(string id, PanelPosition at)
    {
        var entry = Find(id);
        var index = IndexFor(at);
        var current = _entries.IndexOf(entry);
        _entries.RemoveAt(current);
        _entries.Insert(current < index ? index - 1 : index, entry);
    }

    internal void AddAccountLink(Func<string> title, string href) => _accountLinks.Add((title, href));

    internal void AddPages(Assembly assembly) => _pages.Add(assembly);

    internal void AddStylesheet(string path) => _stylesheets.Add(path);

    internal void Customize(Action<PanelLook> look) => look(_look);

    private Entry SectionEntry<TPage>(string id, Func<string> title, Action<SectionSetup>? setup) where TPage : IComponent
    {
        _pages.Add(typeof(TPage).Assembly);
        return new Entry(id, title, false, (shell, current) => shell.AddSection<TPage>(current, setup ?? (_ => { })));
    }

    private static Entry CaptionEntry(string id, Func<string> title) =>
        new(id, title, true, (shell, current) => shell.AddCaption(current));

    private void Insert(Entry entry, PanelPosition? at, string? previous)
    {
        if (_entries.Exists(existing => existing.Id == entry.Id))
            throw new InvalidOperationException($"The panel already has an entry '{entry.Id}'");

        var index = at is not null ? IndexFor(at)
            : previous is not null ? _entries.IndexOf(Find(previous)) + 1
            : _entries.IndexOf(Find(PanelSections.AccessCaption));
        _entries.Insert(index, entry);
    }

    private int IndexFor(PanelPosition at)
    {
        if (at.BeforeEntry is { } before)
            return _entries.IndexOf(Find(before));
        if (at.AfterEntry is { } after)
            return _entries.IndexOf(Find(after)) + 1;
        throw new InvalidOperationException("A panel position names no entry");
    }

    private Entry Find(string id) =>
        _entries.Find(entry => entry.Id == id) ?? throw new InvalidOperationException($"The panel has no entry '{id}'");

    private List<Entry> Visible()
    {
        var shown = _entries.Where(entry => !entry.Hidden).ToList();
        return [.. shown.Where((entry, index) => !entry.IsCaption || (index + 1 < shown.Count && !shown[index + 1].IsCaption))];
    }

    private void ApplyLook(EnfiladeOptions options)
    {
        options.Brand = _look.Brand ?? options.Brand;
        options.Favicon = _look.Favicon ?? options.Favicon;
        options.Accent = _look.Accent ?? options.Accent;
        options.Appearance = _look.Appearance ?? options.Appearance;
    }
}
