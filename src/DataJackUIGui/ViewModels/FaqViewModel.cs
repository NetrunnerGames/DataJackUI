using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataJackUIGui.Services;

namespace DataJackUIGui.ViewModels;

public partial class FaqItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _category = "";

    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    private string _answer = "";

    [ObservableProperty]
    private string? _linkText;

    [ObservableProperty]
    private string? _linkUrl;

    [ObservableProperty]
    private bool _isExpanded;

    public bool HasLink => !string.IsNullOrEmpty(LinkUrl);

    [RelayCommand]
    private void OpenLink()
    {
        if (!string.IsNullOrEmpty(LinkUrl))
        {
            try { SteamService.OpenUrl(LinkUrl); } catch { }
        }
    }
}

public partial class FaqViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ObservableCollection<FaqItemViewModel> _items = [];

    [ObservableProperty]
    private ObservableCollection<FaqItemViewModel> _filteredItems = [];

    public FaqViewModel()
    {
        LoadFaqs();
    }

    partial void OnSearchTextChanged(string value)
    {
        Filter();
    }

    private void Filter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredItems = new ObservableCollection<FaqItemViewModel>(Items);
            return;
        }

        string q = SearchText.Trim().ToLowerInvariant();
        var matches = Items.Where(i =>
            i.Question.ToLowerInvariant().Contains(q) ||
            i.Answer.ToLowerInvariant().Contains(q) ||
            i.Category.ToLowerInvariant().Contains(q));

        FilteredItems = new ObservableCollection<FaqItemViewModel>(matches);
    }

    private void LoadFaqs()
    {
        var list = new List<FaqItemViewModel>
        {
            new()
            {
                Category = "Getting Started",
                Question = "How do I jack in a game or DLC with DataJackUI?",
                Answer = "Go to the 'Jack-in' tab, search for your game by title or Steam App ID, select it, and click 'Jack-in'. DataJackUI will automatically download and stage the verified Lua configuration and manifests into Steam.",
                LinkText = "Join Discord Community",
                LinkUrl = "https://dsc.gg/datajackui"
            },
            new()
            {
                Category = "Getting Started",
                Question = "Do I need to keep Steam running while downloading?",
                Answer = "You can download manifests and game files while Steam is running. However, when switching active unlocker modes or restarting Steam, any running downloads in Steam may momentarily pause while files are applied."
            },
            new()
            {
                Category = "Steam & Plugins",
                Question = "What is the Jack-in Store Plugin?",
                Answer = "The Jack-in plugin integrates an 'Add via DataJackUI' button directly onto official Steam store pages within the Steam desktop client. You can install or update it from the 'Plugin' tab."
            },
            new()
            {
                Category = "Steam & Plugins",
                Question = "What is the difference between IceBreaker and Ost mode?",
                Answer = "IceBreaker is DataJackUI's high-performance native hook (version.dll) that loads custom Lua scripts directly. Ost uses the official OpenSteamTool engine. You can seamlessly switch between them anytime in the 'Mode' tab."
            },
            new()
            {
                Category = "Fixes & Multiplayer",
                Question = "How do multiplayer fixes work in DataJackUI?",
                Answer = "In the 'Fixes' and 'Online Fixes' tabs, you can download community-verified multiplayer patches and bypasses. These connect you to Steam Spacewar or dedicated LAN lobbies so you can play co-op with friends."
            },
            new()
            {
                Category = "Cloud Saves",
                Question = "How do Cloud Saves work?",
                Answer = "DataJackUI supports saving and restoring snapshots of your game progress locally, to Google Drive, or to custom remote storage via Rclone. You can configure and connect your cloud providers under the 'Cloud Saves' tab."
            },
            new()
            {
                Category = "Account & Authentication",
                Question = "Why sign in with Google or Discord?",
                Answer = "Signing in unlocks access to the full manifest catalog and downloads. Google Sign-In uses zero-secret PKCE authentication, and Discord connects your verified roles and server entitlements."
            },
            new()
            {
                Category = "Support & Tutorials",
                Question = "Where can I find video guides and support?",
                Answer = "Our community Discord server hosts step-by-step video tutorials, announcements, troubleshooting guides, and a helpful community ready to answer any questions.",
                LinkText = "Open Support Discord",
                LinkUrl = "https://dsc.gg/datajackui"
            }
        };

        Items = new ObservableCollection<FaqItemViewModel>(list);
        FilteredItems = new ObservableCollection<FaqItemViewModel>(list);
    }
}
