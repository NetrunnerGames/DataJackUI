using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DataJackUIGui.ViewModels;

public partial class FaqPlaceholderItem : ObservableObject
{
    [ObservableProperty]
    private string _category = "";

    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    private string _answer = "";
}

public partial class FaqViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<FaqPlaceholderItem> _placeholderItems = [];

    public FaqViewModel()
    {
        PlaceholderItems = new ObservableCollection<FaqPlaceholderItem>
        {
            new()
            {
                Category = "Consectetur Adipiscing",
                Question = "Lorem ipsum dolor sit amet, consectetur adipiscing elit sed do eiusmod?",
                Answer = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat."
            },
            new()
            {
                Category = "Tempor Incididunt",
                Question = "Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore?",
                Answer = "Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum."
            },
            new()
            {
                Category = "Magna Aliqua",
                Question = "Excepteur sint occaecat cupidatat non proident sunt in culpa qui officia deserunt mollit?",
                Answer = "Curabitur pretium tincidunt lacus. Nulla gravida orci a odio. Nullam varius, turpis et commodo pharetra, est eros bibendum elit, nec luctus magna felis sollicitudin mauris. Integer in mauris eu nibh euismod gravida."
            },
            new()
            {
                Category = "Laboris Nisi",
                Question = "Ullamco laboris nisi ut aliquip ex ea commodo consequat duis aute irure dolor?",
                Answer = "Pellentesque habitant morbi tristique senectus et netus et malesuada fames ac turpis egestas. Proin pharetra nonummy pede. Mauris et orci. Aenean nec lorem. In porttitor."
            },
            new()
            {
                Category = "Officia Deserunt",
                Question = "Sed ut perspiciatis unde omnis iste natus error sit voluptatem accusantium?",
                Answer = "Fusce aliquet pede non pede. Suspendisse dapibus lorem pellentesque magna. Integer nulla donec blandit feugiat ligula. Donec hendrerit, felis et imperdiet euismod, purus ipsum pretium metus. Vivamus quis mi."
            },
            new()
            {
                Category = "Doloremque",
                Question = "Nemo enim ipsam voluptatem quia voluptas sit aspernatur aut odit aut fugit?",
                Answer = "At vero eos et accusamus et iusto odio dignissimos ducimus qui blanditiis praesentium voluptatum deleniti atque corrupti quos dolores et quas molestias excepturi sint occaecati cupiditate non provident."
            }
        };
    }
}
