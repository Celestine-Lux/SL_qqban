using Exiled.API.Features;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneBotIntegration
{
    public class OneBotIntegration : Plugin<Config>
    {
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;

        public override void OnEnabled()
        {
            base.OnEnabled();
            Log.Info("插件已启用，正在启动反向 WebSocket 连接...");
            _cancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => ConnectToOneBot(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }

        public override void OnDisabled()
        {
            Log.Info("插件已禁用，正在关闭 WebSocket 连接...");
            _cancellationTokenSource?.Cancel();
            _webSocket?.Dispose();
            base.OnDisabled();
        }

        private async Task ConnectToOneBot(CancellationToken cancellationToken)
        {
            _webSocket = new ClientWebSocket();

            try
            {
                Log.Info($"正在尝试连接到 OneBot WebSocket 服务器: {Config.OneBotWsUrl}");
                await _webSocket.ConnectAsync(new Uri(Config.OneBotWsUrl), cancellationToken);
                Log.Info("成功连接到 OneBot WebSocket 服务器。");

                while (_webSocket.State == WebSocketState.Open)
                {
                    var buffer = new ArraySegment<byte>(new byte[4096]);
                    var result = await _webSocket.ReceiveAsync(buffer, cancellationToken);

                    var messageBuilder = new StringBuilder();
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer.Array, 0, result.Count));

                    while (!result.EndOfMessage)
                    {
                        result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
                    }

                    var message = messageBuilder.ToString();

                    Log.Debug($"收到原始消息: {message}");

                    if (IsValidJson(message))
                    {
                        HandleMessage(message);
                    }
                    else
                    {
                        Log.Warn($"收到非JSON消息，跳过处理: {message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"WebSocket 连接失败: {ex.Message}");
            }
        }

        private bool IsValidJson(string strInput)
        {
            if (string.IsNullOrWhiteSpace(strInput))
                return false;

            strInput = strInput.Trim();

            if ((strInput.StartsWith("{") && strInput.EndsWith("}")) ||
                (strInput.StartsWith("[") && strInput.EndsWith("]")))
            {
                try
                {
                    JsonConvert.DeserializeObject<dynamic>(strInput);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private void HandleMessage(string jsonMessage)
        {
            try
            {
                Log.Info("正在处理收到的消息...");

                var messageEvent = JsonConvert.DeserializeObject<MessageEvent>(jsonMessage);

                // 检查消息类型（私聊或群聊）
                if (messageEvent.PostType == "message" &&
                    (messageEvent.MessageType == "private" || messageEvent.MessageType == "group"))
                {
                    string rawMessage = messageEvent.RawMessage; // 原始消息内容

                    // 如果 rawMessage 为空，尝试从 message 字段中提取
                    if (string.IsNullOrEmpty(rawMessage))
                    {
                        if (messageEvent.Message is string messageText)
                        {
                            rawMessage = messageText; // 如果 message 是字符串，直接使用
                        }
                        else if (messageEvent.Message is List<MessageSegment> messageSegments)
                        {
                            // 如果 message 是数组，提取文本内容
                            rawMessage = string.Join("", messageSegments
                                .Where(segment => segment.Type == "text")
                                .Select(segment => segment.Data["text"]));
                        }
                    }

                    if (string.IsNullOrEmpty(rawMessage))
                    {
                        Log.Info("消息内容为空，无法处理。");
                        return;
                    }

                    string[] parts = rawMessage.Split(' ');
                    string command = parts[0]; // 命令部分

                    switch (command)
                    {
                        case "/ban_player":
                            Log.Info("收到封禁玩家命令...");
                            if (parts.Length >= 4)
                            {
                                string steam64id = parts[1];
                                int duration = int.Parse(parts[2]);
                                string reason = string.Join(" ", parts.Skip(3));
                                string qqNumber = messageEvent.UserId.ToString();

                                BanPlayer(steam64id, duration, reason, qqNumber).GetAwaiter().GetResult();
                            }
                            else
                            {
                                Log.Info("封禁命令参数不足，请检查格式。");
                                SendQQMessageAsync("封禁命令格式错误，正确格式：/ban_player <steam64id> <时间> <原因>").GetAwaiter().GetResult();
                            }
                            break;

                        case "/unban_player":
                            Log.Info("收到解封玩家命令...");
                            if (parts.Length >= 2)
                            {
                                string steam64id = parts[1];
                                string qqNumber = messageEvent.UserId.ToString();

                                UnbanPlayer(steam64id, qqNumber).GetAwaiter().GetResult();
                            }
                            else
                            {
                                Log.Info("解封命令参数不足，请检查格式。");
                                SendQQMessageAsync("解封命令格式错误，正确格式：/unban_player <steam64id>").GetAwaiter().GetResult();
                            }
                            break;

                        case "/kick_player":
                            Log.Info("收到踢出玩家命令...");
                            if (parts.Length >= 2)
                            {
                                string steam64id = parts[1];
                                KickPlayer(steam64id).GetAwaiter().GetResult();
                            }
                            else
                            {
                                Log.Info("踢出命令参数不足，请检查格式。");
                                SendQQMessageAsync("踢出命令格式错误，正确格式：/kick_player <steam64id>").GetAwaiter().GetResult();
                            }
                            break;

                        case "/cx":
                            Log.Info("收到查询在线人数命令...");
                            QueryOnlinePlayers().GetAwaiter().GetResult();
                            break;

                        case "/adcx":
                            Log.Info("收到AD查询在线人数命令...");
                            string adcxQQNumber = messageEvent.UserId.ToString();
                            ADQueryOnlinePlayers(adcxQQNumber).GetAwaiter().GetResult();
                            break;

                        case "/verify_qq":
                            Log.Info("收到 QQ 号验证命令...");
                            if (parts.Length >= 2)
                            {
                                string qqNumber = parts[1];
                                VerifyQQ(qqNumber).GetAwaiter().GetResult();
                            }
                            else
                            {
                                Log.Info("验证命令参数不足，请检查格式。");
                                SendQQMessageAsync("验证命令格式错误，正确格式：/verify_qq <qq号>").GetAwaiter().GetResult();
                            }
                            break;

                        case "/help":
                            Log.Info("收到帮助命令...");
                            SendHelpMessage().GetAwaiter().GetResult();
                            break;

                        default:
                            if (command.StartsWith("/"))
                            {
                                Log.Info($"未知命令: {command}");
                                SendQQMessageAsync($"未知命令: {command}，请输入 /help 查看可用命令。").GetAwaiter().GetResult();
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"处理消息时出错: {ex.Message}");
            }
        }

        private async Task BanPlayer(string steam64id, int duration, string reason, string qqNumber)
        {
            Log.Info($"正在验证 QQ 号: {qqNumber}");
            if (!Config.AllowedQQs.Contains(qqNumber))
            {
                Log.Info($"QQ {qqNumber} 不在允许的列表中，无法封禁玩家 {steam64id}。");
                await SendQQMessageAsync($"QQ {qqNumber} 不在允许的列表中，无法封禁玩家 {steam64id}。");
                return;
            }

            Log.Info($"正在查找玩家: {steam64id}");
            Player player = Player.Get(steam64id);
            if (player != null)
            {
                Log.Info($"封禁玩家: {player.Nickname} ({steam64id})");
                player.Ban(duration, reason);
                await SendQQMessageAsync($"玩家 {steam64id} 已被封禁，封禁时长：{duration} 分钟，原因：{reason}");
            }
            else
            {
                Log.Info($"玩家 {steam64id} 不在线，尝试离线封禁...");
                bool isBanned = BanManager.OfflineBanPlayer(BanHandler.BanType.UserId, steam64id, reason, TimeSpan.FromMinutes(duration), qqNumber);
                if (isBanned)
                {
                    await SendQQMessageAsync($"玩家 {steam64id} 已被离线封禁，封禁时长：{duration} 分钟，原因：{reason}");
                }
                else
                {
                    await SendQQMessageAsync($"无法封禁玩家 {steam64id}，请检查参数是否正确。");
                }
            }
        }

        private async Task UnbanPlayer(string steam64id, string qqNumber)
        {
            Log.Info($"正在验证 QQ 号: {qqNumber}");
            if (!Config.AllowedQQs.Contains(qqNumber))
            {
                Log.Info($"QQ {qqNumber} 不在允许的列表中，无法解封玩家 {steam64id}。");
                await SendQQMessageAsync($"QQ {qqNumber} 不在允许的列表中，无法解封玩家 {steam64id}。");
                return;
            }

            Log.Info($"正在解封玩家: {steam64id}");
            BanManager.UnbanPlayer(BanHandler.BanType.UserId, steam64id);
            await SendQQMessageAsync($"玩家 {steam64id} 已被解封。");
        }

        private async Task KickPlayer(string steam64id)
        {
            Log.Info($"正在查找玩家: {steam64id}");
            Player player = Player.Get(steam64id);
            if (player != null)
            {
                Log.Info($"踢出玩家: {player.Nickname} ({steam64id})");
                player.Kick("你由于违反服务器规则被踢出，详情联系服务器管理员");
                await SendQQMessageAsync($"玩家 {steam64id} 已被踢出。");
            }
            else
            {
                Log.Info($"未找到玩家: {steam64id}");
                await SendQQMessageAsync($"未找到玩家 {steam64id}。");
            }
        }
        private async Task ADQueryOnlinePlayers(string qqNumber)
        {
            Log.Info($"正在验证 QQ 号: {qqNumber}");

            // 验证QQ号是否在允许列表中
            if (!Config.AllowedQQs.Contains(qqNumber))
            {
                Log.Info($"QQ {qqNumber} 不在允许的列表中，无法使用详细查询功能。");
                await SendQQMessageAsync($"QQ {qqNumber} 不在允许的列表中，无法使用详细查询功能。");
                return;
            }

            try
            {
                // 获取当前在线玩家
                var onlinePlayers = Player.List;

                // 构造消息内容
                string message = $"当前服务器在线人数: {onlinePlayers.Count()}\n";
                message += "在线玩家列表:\n";
                foreach (var player in onlinePlayers)
                {
                    message += $"- {player.Nickname} ({player.UserId})\n";
                }

                // 发送消息到 QQ
                await SendQQMessageAsync(message);
            }
            catch (Exception ex)
            {
                Log.Error($"查询在线人数时出错: {ex.Message}");
                await SendQQMessageAsync("查询在线人数时出错，请稍后重试。");
            }
        }

        private async Task QueryOnlinePlayers()
        {
            try
            {
                // 获取当前在线玩家数量
                int playerCount = Player.List.Count();

                // 构造消息内容 - 只返回人数
                string message = $"当前服务器在线人数: {playerCount}";

                // 发送消息到 QQ
                await SendQQMessageAsync(message);
            }
            catch (Exception ex)
            {
                Log.Error($"查询在线人数时出错: {ex.Message}");
                await SendQQMessageAsync("查询在线人数时出错，请稍后重试。");
            }
        }

        private async Task VerifyQQ(string qqNumber)
        {
            Log.Info($"正在验证 QQ 号: {qqNumber}");
            if (Config.AllowedQQs.Contains(qqNumber))
            {
                Log.Info($"QQ {qqNumber} 已通过验证。");
                await SendQQMessageAsync($"QQ {qqNumber} 已通过验证，可以使用封禁系统。");
            }
            else
            {
                Log.Info($"QQ {qqNumber} 不在允许的列表中。");
                await SendQQMessageAsync($"QQ {qqNumber} 不在允许的列表中，无法使用封禁系统。");
            }
        }

        private async Task SendHelpMessage()
        {
            string helpMessage = "可用命令：\n" +
                                 "/ban_player <steam64id> <时间> <原因> - 封禁玩家\n" +
                                 "/unban_player <steam64id> - 解封玩家\n" +
                                 "/kick_player <steam64id> - 踢出玩家\n" +
                                 "/verify_qq <qq号> - 验证 QQ 号\n" +
                                 "/cx - 查询当前服务器在线人数\n" +
                                 "/adcx - 查询当前服务器在线steam64ID " +
                                 "/help - 显示帮助信息";

            await SendQQMessageAsync(helpMessage);
        }

        private async Task SendQQMessageAsync(string message)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                Log.Error("WebSocket 未连接，无法发送消息。");
                return;
            }

            try
            {
                // 构造消息对象
                var request = new
                {
                    action = "send_group_msg",
                    @params = new
                    {
                        group_id = Config.GroupId.ToString(), // 确保 group_id 是字符串
                        message = message
                    }
                };

                // 将对象序列化为 JSON 字符串
                string jsonMessage = JsonConvert.SerializeObject(request);
                Log.Info($"正在发送消息到 QQ: {jsonMessage}");

                // 发送消息
                byte[] messageBytes = Encoding.UTF8.GetBytes(jsonMessage);
                await _webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                Log.Info("消息已成功发送。");
            }
            catch (Exception ex)
            {
                Log.Error($"发送消息时出错: {ex.Message}");
            }
        }
    }

    public class MessageEvent
    {
        [JsonProperty("post_type")]
        public string PostType { get; set; } // 消息类型

        [JsonProperty("message_type")]
        public string MessageType { get; set; } // 消息子类型（私聊或群聊）

        [JsonProperty("user_id")]
        public string UserId { get; set; } // 发送者 QQ 号

        [JsonProperty("raw_message")]
        public string RawMessage { get; set; } // 原始消息内容

        [JsonProperty("message")]
        public object Message { get; set; } // 消息内容（可能是字符串或数组）

        [JsonProperty("sender")]
        public SenderInfo Sender { get; set; } // 发送者信息
    }

    public class MessageSegment
    {
        [JsonProperty("type")]
        public string Type { get; set; } // 消息段类型（如 text）

        [JsonProperty("data")]
        public Dictionary<string, string> Data { get; set; } // 消息段数据
    }

    public class SenderInfo
    {
        [JsonProperty("user_id")]
        public string UserId { get; set; } // 发送者 QQ 号

        [JsonProperty("nickname")]
        public string Nickname { get; set; } // 发送者昵称

        [JsonProperty("card")]
        public string Card { get; set; } // 发送者群名片
    }
}
