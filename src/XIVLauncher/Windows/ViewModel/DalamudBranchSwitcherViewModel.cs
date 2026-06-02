using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using XIVLauncher.Common.Dalamud;

namespace XIVLauncher.Windows.ViewModel
{
    public class DalamudBranchSwitcherViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<DalamudBranchMeta.Branch> Branches { get; set; } = [];

        private DalamudBranchMeta.Branch selectedBranch;

        public DalamudBranchMeta.Branch SelectedBranch
        {
            get => selectedBranch;
            set
            {
                selectedBranch = value;
                OnPropertyChanged();
            }
        }

        private string appliedBetaKey;

        public string AppliedBetaKey
        {
            get => this.appliedBetaKey;
            set
            {
                this.appliedBetaKey = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public async Task FetchBranchesAsync()
        {
            Branches.Clear();
            var allBranches = await DalamudBranchMeta.FetchBranchesAsync(App.HttpClient);

            // 表示ポリシー:
            //  - 公式トラックは release / stg のみ常時表示(ベータキー不要。key は Meta に含まれて返るため選択だけで適用)。
            //  - 自前トラックはベータキー必須(入力済みキーと一致した時のみ表示)。
            foreach (var branch in allBranches)
            {
                if (DistributionConfig.IsCustomTrack(branch.Track))
                {
                    if (!string.IsNullOrEmpty(branch.Key) && branch.Key == this.AppliedBetaKey)
                        Branches.Add(branch);
                }
                else if (DistributionConfig.IsOfficialVisibleTrack(branch.Track))
                {
                    Branches.Add(branch);
                }
            }

            SelectedBranch = this.Branches.FirstOrDefault(x => x.Track == App.Settings.DalamudBetaKind && x.Key == App.Settings.DalamudBetaKey) ??
                             this.Branches.FirstOrDefault(x => x.Track == "release");
        }
    }
}
