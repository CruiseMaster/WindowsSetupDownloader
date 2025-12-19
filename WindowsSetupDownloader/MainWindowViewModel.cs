using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;

namespace WindowsSetupDownloader
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private bool hasInternet;
        private ObservableCollection<UupBuildInfo> buildInfos;
        private UupBuildInfo selectedBuildInfo;
        private ObservableCollection<string> betriebsysteme;
        private string selectedBetriebsystem;
        private ObservableCollection<string> versionen;
        private ObservableCollection<string> buildsVonVersion;

        public async Task WaitForInternetConnection()
        {
            var answer = string.Empty;
            do
            {
                using (var client = new HttpClient())
                {
                    try
                    {
                        answer = await client.GetStringAsync("https://www.google.de");
                    }
                    catch (Exception)
                    {
                        answer = string.Empty;
                    }
                }
            } while (answer.Equals(string.Empty));

            HasInternet = true;
        }

        public async Task GetBuildInfos()
        {
            var uupDumpApi = new UupDumpApi();
            var result = await uupDumpApi.ListProducts();

            if (result == null)
            {
                HasInternet = false;
                return;
            }

            HasInternet = true;

            var builds = new List<UupBuildInfo>();
            foreach (var build in result.Response.Builds)
            {
                if (build.Arch == "amd64")
                    builds.Add(build);
            }

            result.Response.Builds.Clear();

            BuildInfos = new ObservableCollection<UupBuildInfo>(builds);
            //await using (var fw = new FileStream(Path.Combine(Environment.CurrentDirectory, "output.txt"), FileMode.Create))
            //{
            //    await using (var sr = new StreamWriter(fw))
            //    {
            //        foreach (var build in BuildInfos)
            //        {
            //            await sr.WriteLineAsync(build.Title);
            //        }
            //    }
            //}

            GetBetriebsysteme();

            SelectedBetriebsystem = Betriebsysteme.Last();
        }

        private void GetVersionen()
        {
            var listOfVersions = new List<string>();

            foreach (var build in BuildInfos)
            {
                if (build.Title.StartsWith("Windows") && build.Title.Contains(SelectedBetriebsystem) && build.Title.ToLower().Contains(", version"))
                {
                    if (build.Title.Contains("Preview"))
                        continue;

                    var version = build.Title.Substring(build.Title.ToLower().IndexOf("version") + 7,
                        build.Title.ToLower().IndexOf("(") - build.Title.ToLower().IndexOf("version") - 7);

                    if (!listOfVersions.Contains(version))
                        listOfVersions.Add(version);
                }
            }

            if (SelectedBetriebsystem.Equals("Windows 10") && listOfVersions.Count < 1)
            {
                foreach (var build in BuildInfos)
                {
                    if (build.Title.StartsWith("Feature update to Windows 10,") && build.Title.ToLower().Contains(", version"))
                    {
                        if (build.Title.Contains("Preview"))
                            continue;

                        var version = build.Title.Substring(build.Title.ToLower().IndexOf("version") + 7,
                            build.Title.ToLower().IndexOf("(") - build.Title.ToLower().IndexOf("version") - 7);

                        if (!listOfVersions.Contains(version))
                            listOfVersions.Add(version);
                    }
                }
            }

            Versionen = new ObservableCollection<string>(listOfVersions);
        }

        private void GetBetriebsysteme()
        {
            var listOfBs = new List<string>();
            var listOfBsWithTitle = new List<string>();

            foreach (var build in BuildInfos)
            {
                if (build.Title.Contains(',') && build.Title.StartsWith("Windows"))
                {
                    var bs = build.Title.Substring(0, build.Title.IndexOf(',', 0));
                    if (!listOfBs.Contains(bs))
                    {
                        listOfBs.Add(bs);
                        listOfBsWithTitle.Add(build.Title);
                    }
                }
                else if (build.Title.StartsWith("Feature update to Windows") && build.Title.Contains(','))
                {
                    var bs = build.Title.Substring(18, build.Title.IndexOf(',', 0) - 18);
                    if (!listOfBs.Contains(bs))
                    {
                        listOfBs.Add(bs);
                        listOfBsWithTitle.Add(build.Title);
                    }
                }
            }

            Betriebsysteme = new ObservableCollection<string>(listOfBs);
        }

        public UupBuildInfo SelectedBuildInfo
        {
            get { return selectedBuildInfo; }

            set
            {
                selectedBuildInfo = value;
                OnPropertyChanged();
            }
        }

        public string SelectedBetriebsystem
        {
            get { return selectedBetriebsystem; }

            set
            {
                selectedBetriebsystem = value;
                OnPropertyChanged();

                if (selectedBetriebsystem != null && !selectedBetriebsystem.Trim().Equals(string.Empty))
                {
                    GetVersionen();
                }
            }
        }

        public ObservableCollection<string> Betriebsysteme
        {
            get { return betriebsysteme; }

            set
            {
                betriebsysteme = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> Versionen
        {
            get { return versionen; }

            set
            {
                versionen = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> BuildsVonVersion
        {
            get { return buildsVonVersion; }

            set
            {
                buildsVonVersion = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<UupBuildInfo> BuildInfos
        {
            get { return buildInfos; }

            set
            {
                buildInfos = value;
                OnPropertyChanged();
            }
        }

        public bool HasInternet
        {
            get { return hasInternet; }
            set
            {
                hasInternet = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}