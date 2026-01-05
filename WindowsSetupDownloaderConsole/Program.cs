using SevenZipExtractor;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NtpClient;
using static WindowsSetupDownloaderConsole.ApiModelle;
using Timer = System.Timers.Timer;

namespace WindowsSetupDownloaderConsole
{
    internal class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // Korrektur: Strukturdefinition mit Layout und expliziter Initialisierung
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength; // Muss die Größe der Struktur in Bytes sein
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            // Konstruktor, der dwLength korrekt setzt
            public MEMORYSTATUSEX()
            {
                dwLength = (uint) Marshal.SizeOf<MEMORYSTATUSEX>();
                dwMemoryLoad = 0; // Wird von der Funktion gefüllt
                ullTotalPhys = 0;
                ullAvailPhys = 0;
                ullTotalPageFile = 0;
                ullAvailPageFile = 0;
                ullTotalVirtual = 0;
                ullAvailVirtual = 0;
                ullAvailExtendedVirtual = 0;
            }
        }

        private static List<UupBuildInfo> buildInfos;
        private static UupBuildInfo selectedBuildInfo;
        private static List<string> betriebsysteme;
        private static string selectedBetriebsystem;
        private static List<string> versionen;
        private static string selectedVersion;
        private static List<string> buildsVonVersion;
        private static string selectedBuildVonVersion;
        private static string selectedUUID;

        private static int currentDownloadedFile;
        private static ushort countTooManyRequests;
        private static object lockObject;

        public static async Task Main(string[] args)
        {
            Console.Clear();
            Console.Title = "Microsoft Windows Setup Downloader";
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine("Willkommen beim Microsoft Windows Setup Downloader!");
            Console.WriteLine("Damit du selbst dann Windows installieren kannst, wenn dein USB-Stick zu");
            Console.WriteLine("klein ist, um Windows 7 auf USB-Stick dabei zu haben.\r\n\r\n");
            Console.WriteLine("Ich versuche jetzt eine Verbindung zum Internet herzustellen...");

            currentDownloadedFile = 0;
            countTooManyRequests = 0;
            bool chosen = false;
            lockObject = new object();
            selectedUUID = string.Empty;

            do
            {
                await WaitForInternetConnection();
                await GetBuildInfos();
                GetBetriebsysteme();
                PresentBetriebsysteme();
                PresentVersionen();
                PresentBuilds();
                await GetNeusteVersionUndBuild();
                chosen = ShowSummaryScreen();
            } while (!chosen);

            await GetFileList();
            await UnpackUUPConverter();
            await RunConverter();
        }

        private static async Task AfterSetup()
        {
            short continuation = -1;
            do
            {
                Console.Clear();

                Console.WriteLine("=============================================");
                Console.WriteLine("===============Setup beendet=================");
                Console.WriteLine("=============================================");
                Console.WriteLine(Environment.NewLine);

                Console.WriteLine("Der Setup-Prozess wurde abgebrochen.");
                Console.WriteLine(Environment.NewLine);

                Console.WriteLine("1 => Setup neu starten");
                Console.WriteLine("2 => Den Rechner herunterfahren");
                Console.WriteLine("3 => Den Rechner neu starten");

                Console.WriteLine(Environment.NewLine);
                Console.Write("Wie möchtest du fortfahren? ");

                var eingabe = Console.ReadLine();

                short pEingabe = -1;
                short.TryParse(eingabe, out pEingabe);

                if (pEingabe > 0 && pEingabe < 4)
                    continuation = pEingabe;
            } while (continuation < 1);

            var p = new Process();
            p.StartInfo = new ProcessStartInfo()
            {
                CreateNoWindow = true,
                FileName = "cmd.exe",
                UseShellExecute = false
            };

            if (continuation == 1)
                await RunSetup();
            else if (continuation == 2)
                p.StartInfo.Arguments = " /c shutdown.exe -s -t 5";
            else if (continuation == 3)
                p.StartInfo.Arguments = " /c shutdown.exe -r -t 5";
            else
                await AfterSetup();

            p.Start();
            Console.Clear();
            Console.WriteLine("Das System wird heruntergefahren.");
            Thread.Sleep(4000);
            Environment.Exit(0);
        }

        private static async Task RunSetup()
        {
            var dirList = Directory.GetDirectories("T:\\");
            foreach (var dir in dirList)
            {
                if (dir.ToLower().Contains("release"))
                {
                    var path = Path.Combine("T:", dir, "setup.exe");
                    if (File.Exists(path))
                    {
                        using (var setupProcess = new Process())
                        {
                            setupProcess.StartInfo.FileName = path;
                            setupProcess.Start();
                            await setupProcess.WaitForExitAsync();
                            await AfterSetup();
                        }
                    }
                }
            }
        }

        private static async Task RunConverter()
        {
            if (!File.Exists("T:\\convert-UUP.cmd"))
            {
                Console.WriteLine("Der Konverter ist nicht auf T:");
                return;
            }

            var psi = new ProcessStartInfo()
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                WorkingDirectory = "T:\\",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                Arguments = "/c T:\\convert-UUP.cmd"
            };

            Console.WriteLine("Konvertiere die heruntergeladenen Dateien zu einem Installations-Image...");
            Console.WriteLine(Environment.NewLine);

            using (var p = new Process() {StartInfo = psi})
            {
                p.OutputDataReceived += (sender, data) =>
                {
                    if (data == null || data.Data == null)
                        return;

                    if (data.Data.StartsWith("==="))
                    {
                        Console.SetCursorPosition(0, Console.CursorTop);
                        Console.Write(data.Data);
                    }
                };

                p.ErrorDataReceived += (sender, data) =>
                {
                    if (data == null || data.Data == null)
                        return;

                    using (var fs = new FileStream(Path.Combine(Environment.CurrentDirectory, "Error.txt"), FileMode.Append))
                    {
                        using (var sw = new StreamWriter(fs))
                        {
                            sw.Write(data.Data);
                        }
                    }
                };

                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();

                await p.WaitForExitAsync();

                await RunSetup();
            }
        }

        private static async Task UnpackUUPConverter()
        {
            using (var rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("WindowsSetupDownloaderConsole.UUPConvert.7z"))
            {
                using (var ms = new MemoryStream())
                {
                    await rs.CopyToAsync(ms);
                    ms.Position = 0;

                    using (var af = new ArchiveFile(ms))
                    {
                        af.Extract("T:\\", true);
                    }
                }
            }
        }

        private static async Task DownloadFileList(List<UupGetResponse.UupFile> fileList)
        {
            var options = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 1
            };

            if (!Directory.Exists("T:\\UUPs"))
                Directory.CreateDirectory("T:\\UUPs");

            await Parallel.ForEachAsync(fileList, options, async (file, token) =>
            {
                var success = false;

                lock (lockObject)
                {
                    currentDownloadedFile++;
                    Console.SetCursorPosition(0, Console.CursorTop);
                    Console.Write($"Lade Datei {currentDownloadedFile} von {fileList.Count} Dateien herunter...");
                }

                do
                {
                    using (var httpClient = new HttpClient())
                    {
                        var downloadComplete = false;

                        do
                        {
                            try
                            {
                                var stream = await httpClient.GetStreamAsync(file.Url, token);
                                var ntpConnection = new NtpConnection("time.windows.com");

                                var lastUpdate = ntpConnection.GetUtc();
                                long lastPosition = -1;

                                using (var fs = new FileStream(Path.Combine("T:\\UUPs", file.Name), FileMode.Create))
                                {
                                    try
                                    {
                                        var t = new Timer(5000);
                                        t.AutoReset = true;
                                        t.Elapsed += (sender, e) =>
                                        {
                                            if (lastPosition < fs.Position)
                                            {
                                                lastPosition = fs.Position;
                                                lastUpdate = DateTime.MinValue;

                                                do
                                                {
                                                    try
                                                    {
                                                        lastUpdate = ntpConnection.GetUtc();
                                                    }
                                                    catch (Exception)
                                                    {
                                                        Thread.Sleep(5000);
                                                    }
                                                } while (lastUpdate == DateTime.MinValue);
                                            }

                                            if (DateTime.Now.Subtract(lastUpdate) > new TimeSpan(0, 0, 0, 30))
                                            {
                                                CancellationTokenSource.CreateLinkedTokenSource(token).Cancel(false);
                                                t.Stop();
                                            }
                                        };

                                        t.Start();

                                        await stream.CopyToAsync(fs, token);

                                        t.Stop();
                                        t.Dispose();
                                    }
                                    catch (Exception e)
                                    {
                                        downloadComplete = false;
                                    }

                                    downloadComplete = true;
                                }
                            }
                            catch (Exception e)
                            {
                                downloadComplete = false;
                            }
                        } while (!downloadComplete);
                    }

                    using (var fs = new FileStream(Path.Combine("T:\\UUPs", file.Name), FileMode.Open))
                    {
                        using (var bs = new BufferedStream(fs))
                        {
                            using (var sha1 = new SHA1Managed())
                            {
                                byte[] hash = await sha1.ComputeHashAsync(bs, token);
                                var formatted = new StringBuilder(2 * hash.Length);
                                foreach (var b in hash)
                                {
                                    formatted.AppendFormat("{0:X2}", b);
                                }

                                if (formatted.ToString().ToLower().Equals(file.Sha1.ToLower()))
                                    success = true;
                                else
                                    success = false;
                            }
                        }
                    }
                } while (!success);
            });

            Console.WriteLine(Environment.NewLine + "Download beendet.");
        }

        private static async Task CreateRAMDisk()
        {
            ulong useRAM = 16;

            // Verfügbaren RAM abfragen
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX(); // Verwendung des Konstruktors

            if (GlobalMemoryStatusEx(ref memStatus))
            {
                Console.WriteLine($"Gesamter physikalischer RAM: {memStatus.ullTotalPhys / (1024 * 1024 * 1024)} GB");
                Console.WriteLine($"Verfügbarer physikalischer RAM: {memStatus.ullAvailPhys / (1024 * 1024 * 1024)} GB");

                if (memStatus.ullTotalPhys / (1024 * 1024 * 1024) <= 21)
                {
                    useRAM = (memStatus.ullTotalPhys / (1024 * 1024 * 1024)) - 2;
                }
                else if (memStatus.ullTotalPhys / (1024 * 1024 * 1024) > 21 && memStatus.ullTotalPhys / (1024 * 1024 * 1024) <= 31)
                {
                    useRAM = (memStatus.ullTotalPhys / (1024 * 1024 * 1024)) - 4;
                }
                else
                {
                    useRAM = (memStatus.ullTotalPhys / (1024 * 1024 * 1024)) - 10;
                }
            }
            else
            {
                Console.WriteLine("Das System nennt seine RAM-Größe nicht. Es werden 16 GB für die RAM-Disk verwendet.");
            }

            var p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.Arguments = $"/c imdisk.exe -a -s {useRAM}G -m T: -p \"/fs:NTFS /q /y\"";
            p.Start();
            await p.WaitForExitAsync();

            Console.WriteLine($"RAM-Disk mit {useRAM} GB Speicher erstellt.");
        }

        private static async Task GetFileList()
        {
            Console.Clear();
            Console.WriteLine("Fordere die Dateiliste an...");
            var buildList = buildInfos.Where(e => e.Build.Equals(selectedBuildVonVersion) && e.Title.ToLower().Contains(selectedBetriebsystem.ToLower()))
                .ToList();

            if (!selectedUUID.Equals(string.Empty))
            {
                buildList.Clear();
                buildList.Add(buildInfos.Single(e => e.Uuid.Equals(selectedUUID)));
            }

            var removableBuilds = new List<UupBuildInfo>();
            if (buildList.Count > 1)
            {
                buildList.ForEach(e =>
                {
                    if (e.Title.ToLower().Contains("preview"))
                        removableBuilds.Add(e);
                });

                removableBuilds.ForEach(e => { buildList.Remove(e); });
            }

            if (!buildList.Any())
            {
                Console.WriteLine("Zu dem ausgewählten Build gibt es keine Meta-Informationen.");
                Console.Write("Bitte drücke eine Taste, um mit der Selektion neu zu starten.");
                Console.ReadLine();

                selectedBetriebsystem = string.Empty;
                selectedBuildVonVersion = string.Empty;
                selectedVersion = string.Empty;
                selectedBuildInfo = null;
                selectedUUID = string.Empty;

                await Main(Array.Empty<string>());

                return;
            }
            else if (buildList.Count > 1)
            {
                do
                {
                    Console.Clear();

                    Console.WriteLine("=============================================");
                    Console.WriteLine("===============Commit-Auswahl================");
                    Console.WriteLine("=============================================");
                    Console.WriteLine(Environment.NewLine);

                    Console.WriteLine("Es gibt mehrere Commits für deinen gewählten Build:");
                    Console.WriteLine(Environment.NewLine);

                    for (int i = 0; i < buildList.Count; i++)
                    {
                        Console.WriteLine($"{i + 1} => {buildList[i].Title}");
                    }

                    Console.WriteLine(Environment.NewLine);
                    Console.Write("Welcher Commit soll heruntergeladen werden? ");

                    var eingabe = Console.ReadLine();

                    int pEingabe = -1;
                    int.TryParse(eingabe, out pEingabe);

                    if (pEingabe > 0 && pEingabe < buildList.Count + 1)
                    {
                        var selectedBuild = buildList[pEingabe - 1];

                        buildList.Clear();
                        buildList.Add(selectedBuild);
                    }
                } while (buildList.Count > 1);
            }

            var build = buildList.First();
            var list = new List<UupGetResponse.UupFile>();

            using (var httpClient = new HttpClient())
            {
                var fileListRawText = string.Empty;
                var lines = Array.Empty<string>();

                // APPs
                if (!build.Title.ToLower().Contains("server"))
                {
                    try
                    {
                        fileListRawText = await httpClient.GetStringAsync($"https://uupdump.net/get.php?id={build.Uuid}&pack=neutral&edition=app&aria2=2");

                        if (fileListRawText.ToUpper().Contains("UNSUPPORTED_COMBINATION"))
                        {
                            Console.WriteLine("Zu dem ausgewählten Build gibt es keine Meta-Informationen.");
                            Console.Write("Bitte drücke eine Taste, um mit der Selektion neu zu starten.");
                            Console.ReadLine();

                            selectedBetriebsystem = string.Empty;
                            selectedBuildVonVersion = string.Empty;
                            selectedVersion = string.Empty;
                            selectedBuildInfo = null;
                            selectedUUID = string.Empty;

                            await Main(Array.Empty<string>());

                            return;
                        }
                        else if (fileListRawText.ToUpper().Contains("UNSUPPORTED_LANG"))
                        {
                            Console.WriteLine("Zu dem ausgewählten Build gibt es keine Sprach-Informationen.");
                            Console.Write("Bitte drücke eine Taste, um mit der Selektion neu zu starten.");
                            Console.ReadLine();

                            selectedBetriebsystem = string.Empty;
                            selectedBuildVonVersion = string.Empty;
                            selectedVersion = string.Empty;
                            selectedBuildInfo = null;
                            selectedUUID = string.Empty;

                            await Main(Array.Empty<string>());

                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        Thread.Sleep(10000);

                        if (e.Message.ToLower().Contains("too many requests"))
                            countTooManyRequests++;

                        if (countTooManyRequests > 5)
                        {
                            Console.WriteLine("Der Server ist gerade bitchig und fühlt sich von uns belästigt...");
                            Console.WriteLine("Der beruhigt sich gleich wieder...");

                            countTooManyRequests = 0;
                            Thread.Sleep(30000);
                        }

                        await GetFileList();
                        return;
                    }

                    lines = fileListRawText.Split('\n');

                    for (int i = 0; i < lines.Length; i = i + 4)
                    {
                        if (i + 2 > lines.Length)
                            continue;

                        var entry = new UupGetResponse.UupFile()
                        {
                            Url = lines[i].Trim(),
                            Name = lines[i + 1].Trim().Substring(4),
                            Sha1 = lines[i + 2].Trim().Split('=')[2]
                        };

                        list.Add(entry);
                    }
                }


                // UUPs

                var edition = string.Empty;


                if (build.Title.ToLower().Contains("server"))
                {
                    edition = "serverdatacenter%3Bserverdatacentercore%3Bserverstandard%3Bserverstandardcore";
                }
                else
                {
                    edition = "professional";
                }

                try
                {
                    var url = $"https://uupdump.net/get.php?id={build.Uuid}&pack=de-de&edition={edition}&aria2=2";
                    fileListRawText = await httpClient.GetStringAsync(url);
                }
                catch (Exception e)
                {
                    Thread.Sleep(10000);

                    if (e.Message.ToLower().Contains("too many requests"))
                        countTooManyRequests++;

                    if (countTooManyRequests > 5)
                    {
                        Console.WriteLine("Der Server ist gerade bitchig und fühlt sich von uns belästigt...");
                        Console.WriteLine("Der beruhigt sich gleich wieder...");

                        countTooManyRequests = 0;
                        Thread.Sleep(30000);
                    }

                    await GetFileList();
                    return;
                }

                lines = fileListRawText.Split('\n');

                for (int i = 0; i < lines.Length; i = i + 4)
                {
                    if (i + 2 > lines.Length)
                        continue;

                    var entry = new UupGetResponse.UupFile()
                    {
                        Url = lines[i].Trim(),
                        Name = lines[i + 1].Trim().Substring(4),
                        Sha1 = lines[i + 2].Trim().Split('=')[2]
                    };

                    list.Add(entry);
                }
            }

            Console.WriteLine($"{list.Count} Dateien wurden gefunden.");
            await CreateRAMDisk();
            await DownloadFileList(list);
        }

        private static async Task GetNeusteVersionUndBuild()
        {
            if (!selectedBetriebsystem.Equals("Neuste"))
                return;

            var tmpBuildListe = buildInfos.Where(e => e.Title.ToLower().Contains("version") && e.Title.ToLower().StartsWith("windows") && e.Created != null)
                .ToList();

            var uid = string.Empty;

            using (var httpClient = new HttpClient())
            {
                var htmlText = await httpClient.GetStringAsync("https://uupdump.net/fetchupd.php?arch=amd64&ring=retail");

                uid = htmlText.Substring(htmlText.IndexOf("<code>") + 6, 36);
            }

            if (!uid.Equals(string.Empty))
            {
                var latestBuild = tmpBuildListe.Single(e => e.Uuid.Equals(uid)); //Windows 11, version 25H2 (26200.7171)
                selectedBetriebsystem = latestBuild.Title.Substring(0, latestBuild.Title.IndexOf(','));
                selectedVersion = latestBuild.Title.Substring(latestBuild.Title.ToLower().IndexOf("version") + 8,
                    latestBuild.Title.IndexOf('(') - latestBuild.Title.ToLower().IndexOf("version") - 9);
                selectedBuildVonVersion = latestBuild.Build;
                selectedUUID = latestBuild.Uuid;
            }
        }

        private static bool ShowSummaryScreen()
        {
            Console.Clear();

            Console.WriteLine("=============================================");
            Console.WriteLine("==============Zusammenfassung================");
            Console.WriteLine("=============================================");
            Console.WriteLine(Environment.NewLine);

            Console.WriteLine("Folgende Konfiguration wurde gewählt:");
            Console.WriteLine(Environment.NewLine);

            Console.WriteLine($"Betriebsystem: {selectedBetriebsystem}");
            Console.WriteLine($"Unterversion: {selectedVersion}");
            Console.WriteLine($"Build: {selectedBuildVonVersion}");
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine("1 => Herunterladen und installieren");
            Console.WriteLine("2 => Neu wählen");


            var eingabe = Console.ReadLine();

            int pEingabe = -1;
            int.TryParse(eingabe, out pEingabe);

            if (pEingabe < 0 || pEingabe > 2)
            {
                return ShowSummaryScreen();
            }

            if (pEingabe == 1)
                return true;

            return false;
        }

        private static void PresentBuilds()
        {
            if (selectedBetriebsystem.Equals("Neuste"))
                return;

            Console.Clear();

            Console.WriteLine("=============================================");
            Console.WriteLine("================Wähle Build==================");
            Console.WriteLine("=============================================");
            Console.WriteLine(Environment.NewLine);

            Console.WriteLine("Ich habe folgende Builds gefunden:");

            for (int i = 0; i < buildsVonVersion.Count; i++)
            {
                Console.WriteLine($"{i + 1} => {buildsVonVersion[i]}");
            }

            Console.WriteLine(Environment.NewLine);
            Console.Write("Wähle das zu installierende Build: ");

            var eingabe = Console.ReadLine();

            int pEingabe = -1;
            int.TryParse(eingabe, out pEingabe);

            if (pEingabe < 0 || pEingabe > buildsVonVersion.Count + 1)
            {
                PresentBuilds();
                return;
            }

            selectedBuildVonVersion = buildsVonVersion[pEingabe - 1];
        }

        private static void PresentVersionen()
        {
            if (selectedBetriebsystem.Equals("Neuste"))
                return;

            Console.Clear();

            Console.WriteLine("=============================================");
            Console.WriteLine("============Wähle Unterversion===============");
            Console.WriteLine("=============================================");
            Console.WriteLine(Environment.NewLine);

            Console.WriteLine("Ich habe folgende Unterversionen gefunden:");

            for (int i = 0; i < versionen.Count; i++)
            {
                Console.WriteLine($"{i + 1} => {versionen[i]}");
            }

            Console.WriteLine(Environment.NewLine);
            Console.Write("Wähle die zu installierende Unterversion: ");

            var eingabe = Console.ReadLine();

            int pEingabe = -1;
            int.TryParse(eingabe, out pEingabe);

            if (pEingabe < 0 || pEingabe > betriebsysteme.Count + 1)
            {
                PresentVersionen();
                return;
            }

            selectedVersion = versionen[pEingabe - 1];

            GetBuilds();
        }

        private static void PresentBetriebsysteme()
        {
            Console.Clear();

            Console.WriteLine("=============================================");
            Console.WriteLine("===========Wähle Betriebsystem===============");
            Console.WriteLine("=============================================");
            Console.WriteLine(Environment.NewLine);

            Console.WriteLine("Ich habe folgende Betriebsysteme gefunden:");
            int i = 0;
            for (i = 0; i < betriebsysteme.Count; i++)
            {
                Console.WriteLine($"{i + 1} => {betriebsysteme[i]}");
            }

            Console.WriteLine($"{i + 1} => Neuste Retail-Version (Egal, welches Windows es ist.)");
            Console.WriteLine(Environment.NewLine);
            Console.Write("Wähle das zu installierende Betriebsystem: ");
            var eingabe = Console.ReadLine();

            int pEingabe = -1;
            int.TryParse(eingabe, out pEingabe);

            if (pEingabe < 0 || pEingabe > betriebsysteme.Count + 1)
            {
                PresentBetriebsysteme();
                return;
            }

            if (pEingabe == i + 1)
            {
                selectedBetriebsystem = "Neuste";
            }
            else
            {
                selectedBetriebsystem = betriebsysteme[pEingabe - 1];
                GetVersionen();
            }
        }

        private static async Task WaitForInternetConnection()
        {
            var answer = string.Empty;
            ushort tries = 0;
            do
            {
                using (var client = new HttpClient())
                {
                    try
                    {
                        answer = await client.GetStringAsync("https://www.google.de");
                        //Console.WriteLine("Internet-Antwort:");
                        //Console.WriteLine(answer);
                    }
                    catch (Exception)
                    {
                        answer = string.Empty;
                        tries++;
                        Thread.Sleep(2000);
                        Console.WriteLine("Warte auf Internet...");

                        if (tries == 3)
                        {
                            await LaunchPENetwork();
                            tries = 0;
                        }
                    }
                }
            } while (answer.Equals(string.Empty));
        }

        private static async Task LaunchPENetwork()
        {
            var procs = Process.GetProcesses();

            var peNetworkOpen = false;

            do
            {
                peNetworkOpen = false;

                foreach (var p in procs)
                {
                    if (p.MainWindowTitle.ToLower().Contains("penetwork"))
                    {
                        peNetworkOpen = true;
                    }
                }

                Thread.Sleep(1000);
            } while (peNetworkOpen);


            var newP = new Process();
            newP.StartInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PENetwork", "PENetwork.exe");
            newP.Start();

            Thread.Sleep(3000);

            peNetworkOpen = false;

            do
            {
                peNetworkOpen = false;

                foreach (var p in procs)
                {
                    if (p.MainWindowTitle.ToLower().Contains("penetwork"))
                    {
                        peNetworkOpen = true;
                    }
                }

                Thread.Sleep(1000);
            } while (peNetworkOpen);
        }

        private static void GetBetriebsysteme()
        {
            var listOfBs = new List<string>();
            var listOfBsWithTitle = new List<string>();

            foreach (var build in buildInfos)
            {
                if (build.Title.ToLower().Contains("preview"))
                    continue;

                if (build.Title.ToLower().StartsWith("windows") && build.Title.Contains('(') && build.Title.Contains(')'))
                {
                    var bs = string.Empty;

                    if (build.Title.Contains(','))
                        bs = build.Title.Substring(0, build.Title.IndexOf(',', 0));
                    else
                        bs = build.Title.Substring(0, build.Title.IndexOf('(', 0));

                    bs = bs.Trim();

                    if (!listOfBs.Contains(bs))
                    {
                        listOfBs.Add(bs);
                        listOfBsWithTitle.Add(build.Title);
                    }
                }
                else if (build.Title.ToLower().StartsWith("feature update to ") && build.Title.Contains(','))
                {
                    var bs = build.Title.Substring(18, build.Title.IndexOf(',', 0) - 18);

                    bs = bs.Trim();

                    if (!listOfBs.Contains(bs))
                    {
                        listOfBs.Add(bs);
                        listOfBsWithTitle.Add(build.Title);
                    }
                }
            }

            betriebsysteme = new List<string>(listOfBs);
        }

        private static void GetVersionen()
        {
            var listOfVersions = new List<string>();

            foreach (var build in buildInfos)
            {
                if (build.Title.StartsWith("Windows") && build.Title.Contains(selectedBetriebsystem) && build.Title.ToLower().Contains(", version"))
                {
                    if (build.Title.Contains("Preview"))
                        continue;

                    var version = build.Title.Substring(build.Title.ToLower().IndexOf("version") + 7,
                        build.Title.ToLower().IndexOf("(") - build.Title.ToLower().IndexOf("version") - 7);

                    if (!listOfVersions.Contains(version))
                        listOfVersions.Add(version);
                }
            }

            if (selectedBetriebsystem.Equals("Windows 10") && listOfVersions.Count < 1)
            {
                foreach (var build in buildInfos)
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

            if (selectedBetriebsystem.ToLower().StartsWith("windows server"))
            {
                foreach (var build in buildInfos)
                {
                    if (!build.Title.ToLower().StartsWith("windows server") || build.Title.ToLower().Contains("preview") ||
                        !build.Title.ToLower().Contains(selectedBetriebsystem.ToLower()))
                        continue;

                    if (build.Title.ToLower().Contains(", version"))
                    {
                        var version = build.Title.Substring(build.Title.ToLower().IndexOf("version") + 7,
                            build.Title.ToLower().IndexOf("(") - build.Title.ToLower().IndexOf("version") - 7);

                        if (!listOfVersions.Contains(version))
                            listOfVersions.Add(version);
                    }
                    else if (!listOfVersions.Contains(build.Title))
                    {
                        listOfVersions.Add(build.Title);
                    }
                }
            }

            versionen = new List<string>(listOfVersions);
        }

        private static void GetBuilds()
        {
            var builds = new List<string>();

            foreach (var build in buildInfos)
            {
                if (build.Title.ToLower().Contains(selectedBetriebsystem.ToLower()) && build.Title.ToLower().Contains(selectedVersion.ToLower()))
                {
                    var buildNummer = build.Title.Substring(build.Title.IndexOf('(') + 1, build.Title.IndexOf(')') - build.Title.IndexOf('(') - 1);

                    if (!builds.Contains(buildNummer))
                        builds.Add(buildNummer);
                }
            }

            buildsVonVersion = builds;
        }

        public static async Task GetBuildInfos()
        {
            var uupDumpApi = new UupDumpApi();
            var result = await uupDumpApi.ListProducts();

            var builds = new List<UupBuildInfo>();
            foreach (var build in result.Response.Builds)
            {
                if (build.Arch == "amd64")
                    builds.Add(build);
            }

            result.Response.Builds.Clear();

            buildInfos = new List<UupBuildInfo>(builds);
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
        }
    }
}