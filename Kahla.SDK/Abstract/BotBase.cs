﻿using Aiursoft.Pylon.Interfaces;
using Kahla.Bot.Services;
using Kahla.SDK.Events;
using Kahla.SDK.Models;
using Kahla.SDK.Services;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;

namespace Kahla.SDK.Abstract
{
    public abstract class BotBase : ISingletonDependency
    {
        public AES AES;
        public BotLogger BotLogger;
        public ConversationService ConversationService;
        public FriendshipService FriendshipService;
        public AuthService AuthService;
        public HomeService HomeService;
        public KahlaLocation KahlaLocation;
        public VersionService VersionService;

        public abstract KahlaUser Profile { get; set; }

        public abstract Task OnInit();

        public abstract Task<bool> OnFriendRequest(NewFriendRequestEvent arg);

        public abstract Task OnMessage(string inputMessage, NewMessageEvent eventContext);

        public virtual async Task Start()
        {
            var listenTask = await Connect();

            BotLogger.LogSuccess("Bot started! Waitting for commands. Enter 'help' to view available commands.");
            await Task.WhenAll(listenTask, Command());
        }

        public virtual async Task<Task> Connect()
        {
            var server = AskServerAddress();
            KahlaLocation.UseKahlaServer(server);
            if (!await TestKahlaLive())
            {
                return Task.CompletedTask;
            }
            await OpenSignIn();
            var code = await AskCode();
            await SignIn(code);
            await DisplayMyProfile();
            var websocketAddress = await GetWSAddress();
            BotLogger.LogInfo($"Listening to your account channel: {websocketAddress}");
            var requests = (await FriendshipService.MyRequestsAsync())
                .Items
                .Where(t => !t.Completed);
            foreach (var request in requests)
            {
                await OnNewFriendRequest(new NewFriendRequestEvent
                {
                    RequestId = request.Id,
                    Requester = request.Creator,
                    RequesterId = request.CreatorId,
                });
            }
            return MonitorEvents(websocketAddress);
        }

        public virtual string AskServerAddress()
        {
            BotLogger.LogInfo("Welcome! Please enter the server address of Kahla.");
            BotLogger.LogWarning("\r\nEnter 1 for production\r\nEnter 2 for staging\r\nFor other server, enter like: https://server.kahla.app");
            var result = Console.ReadLine();
            if (result.Trim() == 1.ToString())
            {
                return "https://server.kahla.app";
            }
            else if (result.Trim() == 2.ToString())
            {
                return "https://staging.server.kahla.app";
            }
            else
            {
                return result;
            }
        }

        public virtual async Task<bool> TestKahlaLive()
        {
            try
            {
                BotLogger.LogInfo($"Using Kahla Server: {KahlaLocation}");
                await Task.Delay(200);
                BotLogger.LogInfo("Testing Kahla server connection...");
                await Task.Delay(1000);
                var index = await HomeService.IndexAsync();
                BotLogger.LogSuccess("Success! Your bot is successfully connected with Kahla!\r\n");
                await Task.Delay(200);
                BotLogger.LogInfo($"Server time: \t{index.UTCTime}\tLocal time: \t{DateTime.UtcNow}");
                BotLogger.LogInfo($"Server version: \t{index.APIVersion}\tLocal version: \t{VersionService.GetSDKVersion()}");
                return true;
            }
            catch (Exception e)
            {
                BotLogger.LogDanger(e.Message);
                return false;
            }
        }

        public virtual async Task OpenSignIn()
        {
            BotLogger.LogInfo($"Signing in to Kahla...");
            var address = await AuthService.OAuthAsync();
            BotLogger.LogWarning($"Please open your browser to view this address: ");
            address = address.Split('&')[0] + "&redirect_uri=https%3A%2F%2Flocalhost%3A5000";
            BotLogger.LogWarning(address);
            //410969371
        }

        public virtual async Task<int> AskCode()
        {
            int code;
            while (true)
            {
                await Task.Delay(500);
                BotLogger.LogInfo($"Please enther the `code` in the address bar(after signing in):");
                var codeString = Console.ReadLine().Trim();
                if (!int.TryParse(codeString, out code))
                {
                    BotLogger.LogDanger($"Invalid code! Code is a number! You can find it in the address bar after you sign in.");
                    continue;
                }
                break;
            }
            return code;
        }

        public virtual async Task SignIn(int code)
        {
            while (true)
            {
                try
                {
                    BotLogger.LogInfo($"Calling sign in API with code: {code}...");
                    var response = await AuthService.SignIn(code);
                    if (!string.IsNullOrWhiteSpace(response))
                    {
                        BotLogger.LogSuccess($"Successfully signed in to your account!");
                        break;
                    }
                }
                catch (WebException)
                {
                    BotLogger.LogDanger($"Invalid code!");
                    code = await AskCode();
                }
            }
        }

        public virtual async Task DisplayMyProfile()
        {
            await Task.Delay(200);
            BotLogger.LogInfo($"Getting account profile...");
            var profile = await AuthService.MeAsync();
            Profile = profile.Value;
            await OnInit();
        }

        public virtual async Task<string> GetWSAddress()
        {
            var address = await AuthService.InitPusherAsync();
            await Task.Delay(200);
            return address.ServerPath;
        }

        public virtual Task MonitorEvents(string websocketAddress)
        {
            var exitEvent = new ManualResetEvent(false);
            var url = new Uri(websocketAddress);
            var client = new WebsocketClient(url)
            {
                ReconnectTimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds
            };
            client.ReconnectionHappened.Subscribe(type => BotLogger.LogVerbose($"WebSocket: {type}"));
            client.MessageReceived.Subscribe(OnStargateMessage);
            client.Start();
            return Task.Run(exitEvent.WaitOne);
        }

        public virtual async void OnStargateMessage(ResponseMessage msg)
        {
            var inevent = JsonConvert.DeserializeObject<KahlaEvent>(msg.ToString());
            if (inevent.Type == EventType.NewMessage)
            {
                var typedEvent = JsonConvert.DeserializeObject<NewMessageEvent>(msg.ToString());
                await OnNewMessageEvent(typedEvent);
            }
            else if (inevent.Type == EventType.NewFriendRequestEvent)
            {
                var typedEvent = JsonConvert.DeserializeObject<NewFriendRequestEvent>(msg.ToString());
                await OnNewFriendRequest(typedEvent);
            }
        }

        public virtual async Task OnNewMessageEvent(NewMessageEvent typedEvent)
        {
            string decrypted = AES.OpenSSLDecrypt(typedEvent.Message.Content, typedEvent.AESKey);
            BotLogger.LogInfo($"On message from sender `{typedEvent.Message.Sender.NickName}`: {decrypted}");
            await OnMessage(decrypted, typedEvent).ConfigureAwait(false);
        }

        public virtual async Task OnNewFriendRequest(NewFriendRequestEvent typedEvent)
        {
            BotLogger.LogWarning($"New friend request from '{typedEvent.Requester.NickName}'!");
            var result = await OnFriendRequest(typedEvent);
            await FriendshipService.CompleteRequestAsync(typedEvent.RequestId, result);
            var text = result ? "accepted" : "rejected";
            BotLogger.LogWarning($"Friend request from '{typedEvent.Requester.NickName}' was {text}.");
        }

        public virtual async Task SendMessage(string message, int conversationId, string aesKey)
        {
            var encrypted = AES.OpenSSLEncrypt(message, aesKey);
            await ConversationService.SendMessageAsync(encrypted, conversationId);
        }

        public virtual async Task Command()
        {
            await Task.Delay(0);
            while (true)
            {
                var command = Console.ReadLine();
                if (command.Length < 1)
                {
                    continue;
                }
                switch (command.ToLower().Trim()[0])
                {
                    case 'q':
                        Environment.Exit(0);
                        return;
                    case 'h':
                        BotLogger.LogInfo($"Kahla bot commands:");

                        BotLogger.LogInfo($"\r\nConversation");
                        BotLogger.LogInfo($"\ta\tShow all conversations.");
                        BotLogger.LogInfo($"\ts\tSay something to someone.");
                        BotLogger.LogInfo($"\tb\tBroadcast to all conversations.");

                        BotLogger.LogInfo($"\r\nGroup");
                        BotLogger.LogInfo($"\tm\tMute all groups.");
                        BotLogger.LogInfo($"\tu\tUnmute all groups.");

                        BotLogger.LogInfo($"\r\nNetwork");
                        BotLogger.LogInfo($"\tr\tReconnect to Stargate.");
                        BotLogger.LogInfo($"\tl\tLogout.");

                        BotLogger.LogInfo($"\r\nProgram");
                        BotLogger.LogInfo($"\th\tShow help.");
                        BotLogger.LogInfo($"\tq\tQuit bot.");
                        break;
                    default:
                        BotLogger.LogDanger($"Unknown command: {command}. Please try command: 'h' for help.");
                        break;
                }
            }
        }
    }
}
