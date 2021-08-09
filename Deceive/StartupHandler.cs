﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;
using Deceive.Properties;

namespace Deceive
{
    internal static class StartupHandler
    {
        internal static string DeceiveTitle => "Deceive " + Utils.DeceiveVersion;

        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
            try
            {
                StartDeceive(args);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                // Show some kind of message so that Deceive doesn't just disappear.
                MessageBox.Show(
                    "Deceive bir hatayla karşılaştı ve kendisini düzgün şekilde başlatamadı. " +
                    "Lütfen içerik oluşturucuyla GitHub (https://github.com/molenzwiebel/deceive) veya Discord aracılığıyla iletişime geçin.\n\n" + ex,
                    DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );
            }
        }

        /**
         * Actual main function. Wrapped into a separate function so we can catch exceptions.
         */
        private static void StartDeceive(string[] cmdArgs)
        {
            // We are supposed to launch league, so if it's already running something is going wrong.
            if (Utils.IsClientRunning() && cmdArgs.All(x => x.ToLower() != "--allow-multiple-clients"))
            {
                var result = MessageBox.Show(
                    "Riot İstemcisi şu anda çalışıyor. Çevrimiçi durumunuzu maskelemek için, Riot İstemcisinin Deceive tarafından başlatılması gerekir. " +
                    "Deceive'ın uygun konfigürasyonla yeniden başlatılabilmesi için Riot İstemcisini ve onun tarafından başlatılan oyunları durdurmasını istiyor musunuz?",
                    DeceiveTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1
                );

                if (result != DialogResult.Yes) return;
                Utils.KillProcesses();
                Thread.Sleep(2000); // Riot Client takes a while to die
            }

            try
            {
                File.WriteAllText(Path.Combine(Utils.DataDir, "debug.log"), string.Empty);
                Debug.Listeners.Add(new TextWriterTraceListener(Path.Combine(Utils.DataDir, "debug.log")));
                Debug.AutoFlush = true;
                Trace.WriteLine(DeceiveTitle);
            }
            catch
            {
                // ignored; just don't save logs if file is already being accessed
            }

            // Step 0: Check for updates in the background.
            Utils.CheckForUpdates();

            // Step 1: Open a port for our chat proxy, so we can patch chat port into clientconfig.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint) listener.LocalEndpoint).Port;

            // Step 2: Find the Riot Client.
            var riotClientPath = Utils.GetRiotClientPath();

            // If the riot client doesn't exist, the user is either severely outdated or has a bugged install.
            if (riotClientPath == null)
            {
                MessageBox.Show(
                    "Deceive, Riot İstemcisine giden yolu bulamadı. Oyunu yüklediyseniz ve düzgün çalışıyorsa, lütfen GitHub (https://github.com/molenzwiebel/deceive) veya Discord aracılığıyla bir hata raporu gönderin.",
                    DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );

                return;
            }

            // Step 3: Start proxy web server for clientconfig
            var proxyServer = new ConfigProxy("https://clientconfig.rpg.riotgames.com", port);

            // Step 4: Start the Riot Client and wait for a connect.
            var game = "valorant";
            if (cmdArgs.Any(x => x.ToLower() == "lor"))
            {
                game = "bacon";
            }

            if (cmdArgs.Any(x => x.ToLower() == "valorant"))
            {
                game = "valorant";
            }

            var startArgs = new ProcessStartInfo
            {
                FileName = riotClientPath,
                Arguments = $"--client-config-url=\"http://127.0.0.1:{proxyServer.ConfigPort}\" --launch-product={game} --launch-patchline=live"
            };
            if (cmdArgs.Any(x => x.ToLower() == "--allow-multiple-clients")) startArgs.Arguments += " --allow-multiple-clients";
            var riotClient = Process.Start(startArgs);
            // Kill Deceive when Riot Client has exited, so no ghost Deceive exists.
            if (riotClient != null)
            {
                riotClient.EnableRaisingEvents = true;
                riotClient.Exited += (sender, args) =>
                {
                    Trace.WriteLine("Exiting on Riot Client exit.");
                    Environment.Exit(0);
                };
            }

            // Step 5: Get chat server and port for this player by listening to event from ConfigProxy.
            string chatHost = null;
            var chatPort = 0;
            proxyServer.PatchedChatServer += (sender, args) =>
            {
                chatHost = args.ChatHost;
                chatPort = args.ChatPort;
            };

            var incoming = listener.AcceptTcpClient();

            // Step 6: Connect sockets.
            var sslIncoming = new SslStream(incoming.GetStream());
            var cert = new X509Certificate2(Resources.Certificate);
            sslIncoming.AuthenticateAsServer(cert);

            if (chatHost == null)
            {
                MessageBox.Show(
                    "Deceive, Riot'un sohbet sunucusunu bulamadı, lütfen daha sonra tekrar deneyin. Bu sorun devam ederse ve Deceive olmadan sohbete normal şekilde bağlanabiliyorsanız, lütfen GitHub (https://github.com/molenzwiebel/deceive) veya Discord üzerinden bir hata raporu gönderin.",
                    DeceiveTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1
                );
                return;
            }

            var outgoing = new TcpClient(chatHost, chatPort);
            var sslOutgoing = new SslStream(outgoing.GetStream());
            sslOutgoing.AuthenticateAsClient(chatHost);

            // Step 7: All sockets are now connected, start tray icon.
            var mainController = new MainController();
            mainController.StartThreads(sslIncoming, sslOutgoing);
            mainController.ConnectionErrored += (sender, args) =>
            {
                Trace.WriteLine("Trying to reconnect.");
                sslIncoming.Close();
                sslOutgoing.Close();
                incoming.Close();
                outgoing.Close();

                incoming = listener.AcceptTcpClient();
                sslIncoming = new SslStream(incoming.GetStream());
                sslIncoming.AuthenticateAsServer(cert);
                while (true)
                {
                    try
                    {
                        outgoing = new TcpClient(chatHost, chatPort);
                        break;
                    }
                    catch (SocketException e)
                    {
                        Trace.WriteLine(e);
                        var result = MessageBox.Show(
                            "Deceive, Riot'un sohbet sunucusunu bulamadı, lütfen daha sonra tekrar deneyin. Bu sorun devam ederse ve Deceive olmadan sohbete normal şekilde bağlanabiliyorsanız, lütfen GitHub (https://github.com/molenzwiebel/deceive) veya Discord üzerinden bir hata raporu gönderin..",
                            DeceiveTitle,
                            MessageBoxButtons.RetryCancel,
                            MessageBoxIcon.Error,
                            MessageBoxDefaultButton.Button1
                        );
                        if (result == DialogResult.Cancel)
                        {
                            Environment.Exit(0);
                        }
                    }
                }

                sslOutgoing = new SslStream(outgoing.GetStream());
                sslOutgoing.AuthenticateAsClient(chatHost);
                mainController.StartThreads(sslIncoming, sslOutgoing);
            };
            Application.EnableVisualStyles();
            Application.Run(mainController);
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            //Log all unhandled exceptions
            Trace.WriteLine(e.ExceptionObject as Exception);
            Trace.WriteLine(Environment.StackTrace);
        }
    }
}