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

        /// <summary>
        ///     [estell] 一覧取得時に、取得できなかった配布元があったか。
        ///     true のとき、現在のトラックが一覧に無くても既定(release)へ勝手に切り替えない。
        /// </summary>
        public bool HasIncompleteBranchList { get; private set; }

        public async Task FetchBranchesAsync()
        {
            Branches.Clear();
            var (allBranches, officialOk, customOk) = await DalamudBranchMeta.FetchBranchesDetailedAsync(App.HttpClient);

            var customConfigured = !string.IsNullOrEmpty(DistributionConfig.CustomMetaUrl);
            this.HasIncompleteBranchList = !officialOk || (customConfigured && !customOk);

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

            var current = this.Branches.FirstOrDefault(
                x => x.Track == App.Settings.DalamudBetaKind && x.Key == App.Settings.DalamudBetaKey);

            if (current != null)
            {
                SelectedBranch = current;
                return;
            }

            // [estell] 現在のトラックが一覧に無い場合、本家は無条件で release を選んでいた。
            // 配布元が一時的に落ちているだけのときにこれをやると、画面を開いただけで
            // 設定が公式 release へ書き換わってしまう(2026-08-17 の GitHub 障害時に発生)。
            // 一覧が不完全なときは選択を変更せず、ユーザーの設定を保持する。
            if (this.HasIncompleteBranchList)
            {
                SelectedBranch = null;
                return;
            }

            SelectedBranch = this.Branches.FirstOrDefault(x => x.Track == "release");
        }
    }
}
