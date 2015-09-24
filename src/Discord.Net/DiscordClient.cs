﻿using Discord.Collections;
using Discord.Helpers;
using Discord.Net;
using Discord.Net.API;
using Discord.Net.WebSockets;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Discord
{
	public enum DiscordClientState : byte
	{
		Disconnected,
		Connecting,
		Connected,
		Disconnecting
	}

	/// <summary> Provides a connection to the DiscordApp service. </summary>
	public partial class DiscordClient
	{
		private readonly Random _rand;
		private readonly DiscordAPIClient _api;
		private readonly DataWebSocket _dataSocket;
		private readonly VoiceWebSocket _voiceSocket;
		private readonly ConcurrentQueue<Message> _pendingMessages;
		private readonly ManualResetEvent _disconnectedEvent;
		private readonly ManualResetEventSlim _connectedEvent;
		private readonly JsonSerializer _serializer;
		private Task _runTask;
		private string _token;

		protected ExceptionDispatchInfo _disconnectReason;
		private bool _wasDisconnectUnexpected;

		/// <summary> Returns the id of the current logged-in user. </summary>
		public string CurrentUserId => _currentUserId;
		private string _currentUserId;
		/// <summary> Returns the current logged-in user. </summary>
		public User CurrentUser => _currentUser;
        private User _currentUser;
		/// <summary> Returns the id of the server this user is currently connected to for voice. </summary>
		public string CurrentVoiceServerId => _voiceSocket.CurrentVoiceServerId;
		/// <summary> Returns the server this user is currently connected to for voice. </summary>
		public Server CurrentVoiceServer => _servers[_voiceSocket.CurrentVoiceServerId];

		/// <summary> Returns the current connection state of this client. </summary>
		public DiscordClientState State => (DiscordClientState)_state;
		private int _state;

		/// <summary> Returns the configuration object used to make this client. Note that this object cannot be edited directly - to change the configuration of this client, use the DiscordClient(DiscordClientConfig config) constructor. </summary>
		public DiscordClientConfig Config => _config;
		private readonly DiscordClientConfig _config;

		/// <summary> Returns a collection of all channels this client is a member of. </summary>
		public Channels Channels => _channels;
		private readonly Channels _channels;
		/// <summary> Returns a collection of all user-server pairs this client can currently see. </summary>
		public Members Members => _members;
		private readonly Members _members;
		/// <summary> Returns a collection of all messages this client has seen since logging in and currently has in cache. </summary>
		public Messages Messages => _messages;
		private readonly Messages _messages;
		//TODO: Do we need the roles cache?
		/// <summary> Returns a collection of all role-server pairs this client can currently see. </summary>
		public Roles Roles => _roles;
		private readonly Roles _roles;
		/// <summary> Returns a collection of all servers this client is a member of. </summary>
		public Servers Servers => _servers;
		private readonly Servers _servers;
		/// <summary> Returns a collection of all users this client can currently see. </summary>
		public Users Users => _users;
		private readonly Users _users;

		public CancellationToken CancelToken => _cancelToken;
		private CancellationTokenSource _cancelTokenSource;
		private CancellationToken _cancelToken;

		/// <summary> Initializes a new instance of the DiscordClient class. </summary>
		public DiscordClient(DiscordClientConfig config = null)
		{
			_config = config ?? new DiscordClientConfig();
			_config.Lock();

			_state = (int)DiscordClientState.Disconnected;
			_cancelToken = new CancellationToken(true);
			_disconnectedEvent = new ManualResetEvent(true);
			_connectedEvent = new ManualResetEventSlim(false);
			_rand = new Random();

			_api = new DiscordAPIClient(_config.LogLevel, _config.APITimeout);
			_dataSocket = new DataWebSocket(this);
			_dataSocket.Connected += (s, e) => { if (_state == (int)DiscordClientState.Connecting) CompleteConnect(); };
			_dataSocket.Disconnected += async (s, e) => 
			{
				RaiseDisconnected(e);
				if (e.WasUnexpected)
					await _dataSocket.Reconnect(_token);
			};
			if (_config.EnableVoice)
			{
				_voiceSocket = new VoiceWebSocket(this);
				_voiceSocket.Connected += (s, e) => RaiseVoiceConnected();
				_voiceSocket.Disconnected += async (s, e) =>
				{
					foreach (var member in _members)
					{
						if (member.IsSpeaking)
						{
							member.IsSpeaking = false;
							RaiseUserIsSpeaking(member, false);
						}
					}
					RaiseVoiceDisconnected(e);
					if (e.WasUnexpected)
						await _voiceSocket.Reconnect();
				};
				_voiceSocket.IsSpeaking += (s, e) =>
				{
					if (_voiceSocket.State == WebSocketState.Connected)
					{
						var member = _members[e.UserId, _voiceSocket.CurrentVoiceServerId];
						bool value = e.IsSpeaking;
                        if (member.IsSpeaking != value)
						{
							member.IsSpeaking = value;
							RaiseUserIsSpeaking(member, value);
							if (_config.TrackActivity)
								member.UpdateActivity();
						}
					}
				};
            }

			object cacheLock = new object();
			_channels = new Channels(this, cacheLock);
			_members = new Members(this, cacheLock);
			_messages = new Messages(this, cacheLock);
			_roles = new Roles(this, cacheLock);
			_servers = new Servers(this, cacheLock);
			_users = new Users(this, cacheLock);

			_dataSocket.LogMessage += (s, e) => RaiseOnLog(e.Severity, LogMessageSource.DataWebSocket, e.Message);
			if (_config.EnableVoice)
				_voiceSocket.LogMessage += (s, e) => RaiseOnLog(e.Severity, LogMessageSource.VoiceWebSocket, e.Message);
			if (_config.LogLevel >= LogMessageSeverity.Info)
			{
				_dataSocket.Connected += (s, e) => RaiseOnLog(LogMessageSeverity.Info, LogMessageSource.DataWebSocket, "Connected");
				_dataSocket.Disconnected += (s, e) => RaiseOnLog(LogMessageSeverity.Info, LogMessageSource.DataWebSocket, "Disconnected");
				//_dataSocket.ReceivedEvent += (s, e) => RaiseOnLog(LogMessageSeverity.Info, LogMessageSource.DataWebSocket, $"Received {e.Type}");
				if (_config.EnableVoice)
				{
					_voiceSocket.Connected += (s, e) => RaiseOnLog(LogMessageSeverity.Info, LogMessageSource.VoiceWebSocket, "Connected");
					_voiceSocket.Disconnected += (s, e) => RaiseOnLog(LogMessageSeverity.Info, LogMessageSource.VoiceWebSocket, "Disconnected");
				}
			}
			if (_config.LogLevel >= LogMessageSeverity.Verbose)
			{
				bool isDebug = _config.LogLevel >= LogMessageSeverity.Debug;
				ServerCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Created Server: {e.Server?.Name}" + 
					(isDebug ? $" ({e.ServerId})" : ""));
				ServerDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Destroyed Server: {e.Server?.Name}" + 
					(isDebug ? $" ({e.ServerId})" : ""));
				ServerUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Updated Server: {e.Server?.Name}" + 
					(isDebug ? $" ({e.ServerId})" : ""));
				UserIsTyping += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Updated User (Is Typing): {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}/{e.User.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId}/{e.UserId})" : ""));
				ChannelCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Created Channel: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId})" : ""));
				ChannelDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Destroyed Channel: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId})" : ""));
				ChannelUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Updated Channel: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId})" : ""));
				MessageCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Created Message: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}/{e.MessageId}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId}/{e.MessageId})" : ""));
				MessageDeleted += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Deleted Message: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}/{e.MessageId}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId}/{e.MessageId})" : ""));
				MessageUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Updated Message: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}/{e.MessageId}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId}/{e.MessageId})" : ""));
				MessageReadRemotely += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Read Message (Remotely): {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}/{e.MessageId}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId}/{e.MessageId})" : ""));
				MessageSent += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Sent Message: {e.Server?.Name ?? "[Private]"}/{e.Channel.Name}/{e.MessageId}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.ChannelId}/{e.MessageId})" : ""));
				RoleCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Created Role: {e.Server?.Name ?? "[Private]"}/{e.Role.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.RoleId})." : ""));
				RoleUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Updated Role: {e.Server?.Name ?? "[Private]"}/{e.Role.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.RoleId})." : ""));
				RoleDeleted += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Deleted Role: {e.Server?.Name ?? "[Private]"}/{e.Role.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.RoleId})." : ""));
				BanAdded += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Added Ban: {e.Server?.Name ?? "[Private]"}/{e.User?.Name ?? "Unknown"}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.UserId})." : ""));
				BanRemoved += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Removed Ban: {e.Server?.Name ?? "[Private]"}/{e.User?.Name ?? "Unknown"}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.UserId})." : ""));
				UserAdded += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Added Member: {e.Server?.Name ?? "[Private]"}/{e.User.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.UserId})." : ""));
				UserRemoved += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Removed Member: {e.Server?.Name ?? "[Private]"}/{e.User.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.UserId})." : ""));
				UserUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Updated User: {e.User.Name}" +
					(isDebug ? $" ({e.UserId})." : ""));
				MemberUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client,
					$"Updated Member: {e.Server?.Name ?? "[Private]"}/{e.User.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.UserId})." : ""));
				UserPresenceUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Updated Member (Presence): {e.Server?.Name ?? "[Private]"}/{e.User.Name}" +
					(isDebug ? $" ({e.ServerId ?? "[Private]"}/{e.UserId})" : ""));
				UserVoiceStateUpdated += (s, e) => RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Client, 
					$"Updated Member (Voice State): {e.Server?.Name ?? "[Private]"}/{e.User.Name}" +
					(isDebug ? $" ({e.ServerId ?? "0"}/{e.UserId})" : ""));
				
				_api.RestClient.OnRequest += (s, e) =>
				{
					if (e.Payload != null)
						RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Rest, $"{e.Method.Method} {e.Path}: {Math.Round(e.ElapsedMilliseconds, 2)} ms ({e.Payload})");
					else
						RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Rest, $"{e.Method.Method} {e.Path}: {Math.Round(e.ElapsedMilliseconds, 2)} ms");
				};
			}
			if (_config.LogLevel >= LogMessageSeverity.Debug)
			{
				_channels.ItemCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Created Channel {e.Item?.ServerId ?? "[Private]"}/{e.Item.Id}");
				_channels.ItemDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Destroyed Channel {e.Item.ServerId ?? "[Private]"}/{e.Item.Id}");
				_channels.Cleared += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Cleared Channels");
				_members.ItemCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Created Member {e.Item.ServerId ?? "[Private]"}/{e.Item.UserId}");
				_members.ItemDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Destroyed Member {e.Item.ServerId ?? "[Private]"}/{e.Item.UserId}");
				_members.Cleared += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Cleared Members");
				_messages.ItemCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Created Message {e.Item.ServerId ?? "[Private]"}/{e.Item.ChannelId}/{e.Item.Id}");
				_messages.ItemDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Destroyed Message {e.Item.ServerId ?? "[Private]"}/{e.Item.ChannelId}/{e.Item.Id}");
				_messages.ItemRemapped += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Remapped Message {e.Item.ServerId ?? "[Private]"}/{e.Item.ChannelId}/[{e.OldId} -> {e.NewId}]");
				_messages.Cleared += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Cleared Messages");
				_roles.ItemCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Created Role {e.Item.ServerId}/{e.Item.Id}");
				_roles.ItemDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Destroyed Role {e.Item.ServerId}/{e.Item.Id}");
				_roles.Cleared += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Cleared Roles");
				_servers.ItemCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Created Server {e.Item.Id}");
				_servers.ItemDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Destroyed Server {e.Item.Id}");
				_servers.Cleared += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Cleared Servers");
				_users.ItemCreated += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Created User {e.Item.Id}");
				_users.ItemDestroyed += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Destroyed User {e.Item.Id}");
				_users.Cleared += (s, e) => RaiseOnLog(LogMessageSeverity.Debug, LogMessageSource.Cache, $"Cleared Users");
			}

			if (_config.UseMessageQueue)
				_pendingMessages = new ConcurrentQueue<Message>();

			_serializer = new JsonSerializer();
#if TEST_RESPONSES
			_serializer.CheckAdditionalContent = true;
			_serializer.MissingMemberHandling = MissingMemberHandling.Error;
#endif

			_dataSocket.ReceivedEvent += async (s, e) =>
			{
				switch (e.Type)
				{
					//Global
					case "READY": //Resync
						{
							var data = e.Payload.ToObject<Events.Ready>(_serializer);
							_currentUserId = data.User.Id;
							_currentUser = _users.GetOrAdd(data.User.Id);
							_currentUser.Update(data.User);
							foreach (var model in data.Guilds)
							{
								var server = _servers.GetOrAdd(model.Id);
								server.Update(model);
							}
							foreach (var model in data.PrivateChannels)
							{
								var user = _users.GetOrAdd(model.Recipient.Id);
								user.Update(model.Recipient);
								var channel = _channels.GetOrAdd(model.Id, null, user.Id);
								channel.Update(model);
							}
						}
						break;
					case "RESUMED":
						break;

					//Servers
					case "GUILD_CREATE":
						{
							var model = e.Payload.ToObject<Events.GuildCreate>(_serializer);
							var server = _servers.GetOrAdd(model.Id);
							server.Update(model);
							RaiseServerCreated(server);
						}
						break;
					case "GUILD_UPDATE":
						{
							var model = e.Payload.ToObject<Events.GuildUpdate>(_serializer);
							var server = _servers[model.Id];
							if (server != null)
							{
								server.Update(model);
								RaiseServerUpdated(server);
							}
						}
						break;
					case "GUILD_DELETE":
						{
							var data = e.Payload.ToObject<Events.GuildDelete>(_serializer);
							var server = _servers.TryRemove(data.Id);
							if (server != null)
								RaiseServerDestroyed(server);
						}
						break;

					//Channels
					case "CHANNEL_CREATE":
						{
							var data = e.Payload.ToObject<Events.ChannelCreate>(_serializer);
							Channel channel;
							if (data.IsPrivate)
							{
								var user = _users.GetOrAdd(data.Recipient.Id);
								user.Update(data.Recipient);
								channel = _channels.GetOrAdd(data.Id, null, user.Id);
							}
							else
								channel = _channels.GetOrAdd(data.Id, data.GuildId, null);
							channel.Update(data);
							RaiseChannelCreated(channel);
						}
						break;
					case "CHANNEL_UPDATE":
						{
							var data = e.Payload.ToObject<Events.ChannelUpdate>(_serializer);
							var channel = _channels[data.Id];
							if (channel != null)
							{
								channel.Update(data);
								RaiseChannelUpdated(channel);
							}
						}
						break;
					case "CHANNEL_DELETE":
						{
							var data = e.Payload.ToObject<Events.ChannelDelete>(_serializer);
							var channel = _channels.TryRemove(data.Id);
							if (channel != null)
								RaiseChannelDestroyed(channel);
						}
						break;

					//Members
					case "GUILD_MEMBER_ADD":
						{
							var data = e.Payload.ToObject<Events.GuildMemberAdd>(_serializer);
							var user = _users.GetOrAdd(data.User.Id);
							var member = _members.GetOrAdd(data.User.Id, data.GuildId);
							user.Update(data.User);
							member.Update(data);
							if (_config.TrackActivity)
								member.UpdateActivity();
							RaiseUserAdded(member);
						}
						break;
					case "GUILD_MEMBER_UPDATE":
						{
							var data = e.Payload.ToObject<Events.GuildMemberUpdate>(_serializer);
							var member = _members[data.User.Id, data.GuildId];
							if (member != null)
							{
								member.Update(data);
								RaiseMemberUpdated(member);
							}
						}
						break;
					case "GUILD_MEMBER_REMOVE":
						{
							var data = e.Payload.ToObject<Events.GuildMemberRemove>(_serializer);
							var member = _members.TryRemove(data.UserId, data.GuildId);
							if (member != null)
								RaiseUserRemoved(member);
						}
						break;

					//Roles
					case "GUILD_ROLE_CREATE":
						{
							var data = e.Payload.ToObject<Events.GuildRoleCreate>(_serializer);
							var role = _roles.GetOrAdd(data.Data.Id, data.GuildId);
							role.Update(data.Data);
							RaiseRoleUpdated(role);
						}
						break;
					case "GUILD_ROLE_UPDATE":
						{
							var data = e.Payload.ToObject<Events.GuildRoleUpdate>(_serializer);
							var role = _roles[data.Data.Id];
							if (role != null)
								role.Update(data.Data);
							RaiseRoleUpdated(role);
						}
						break;
					case "GUILD_ROLE_DELETE":
						{
							var data = e.Payload.ToObject<Events.GuildRoleDelete>(_serializer);
							var role = _roles.TryRemove(data.RoleId);
							if (role != null)
								RaiseRoleDeleted(role);
						}
						break;

					//Bans
					case "GUILD_BAN_ADD":
						{
							var data = e.Payload.ToObject<Events.GuildBanAdd>(_serializer);
							var server = _servers[data.GuildId];
							if (server != null)
							{
								server.AddBan(data.UserId);
								RaiseBanAdded(data.UserId, server);
							}
						}
						break;
					case "GUILD_BAN_REMOVE":
						{
							var data = e.Payload.ToObject<Events.GuildBanRemove>(_serializer);
							var server = _servers[data.GuildId];
							if (server != null && server.RemoveBan(data.UserId))
								RaiseBanRemoved(data.UserId, server);
						}
						break;

					//Messages
					case "MESSAGE_CREATE":
						{
							var data = e.Payload.ToObject<Events.MessageCreate>(_serializer);
							Message msg = null;

							bool wasLocal = _config.UseMessageQueue && data.Author.Id == _currentUserId && data.Nonce != null;
							if (wasLocal)
							{
								msg = _messages.Remap("nonce" + data.Nonce, data.Id);
								if (msg != null)
								{
									msg.IsQueued = false;
									msg.Id = data.Id;
								}
							}
							
							if (msg == null)
								msg = _messages.GetOrAdd(data.Id, data.ChannelId, data.Author.Id);
							msg.Update(data);
							if (_config.TrackActivity)
							{
								var channel = msg.Channel;
								if (channel == null || channel.IsPrivate)
								{
									var user = msg.User;
									if (user != null)
										user.UpdateActivity(data.Timestamp);
								}
								else
								{
									var member = msg.Member;
									if (member != null)
										member.UpdateActivity(data.Timestamp);
								}
							}
							if (wasLocal)
								RaiseMessageSent(msg);
							RaiseMessageCreated(msg);
						}
						break;
					case "MESSAGE_UPDATE":
						{
							var data = e.Payload.ToObject<Events.MessageUpdate>(_serializer);
							var msg = _messages[data.Id];
                            if (msg != null)
							{
								msg.Update(data);
								RaiseMessageUpdated(msg);
							}
						}
						break;
					case "MESSAGE_DELETE":
						{
							var data = e.Payload.ToObject<Events.MessageDelete>(_serializer);
							var msg = _messages.TryRemove(data.Id);
							if (msg != null)
								RaiseMessageDeleted(msg);
						}
						break;
					case "MESSAGE_ACK":
						{
							var data = e.Payload.ToObject<Events.MessageAck>(_serializer);
							var msg = GetMessage(data.MessageId);
							if (msg != null)
								RaiseMessageReadRemotely(msg);
						}
						break;

					//Statuses
					case "PRESENCE_UPDATE":
						{
							var data = e.Payload.ToObject<Events.PresenceUpdate>(_serializer);
							var member = _members[data.User.Id, data.GuildId];
							/*if (_config.TrackActivity)
							{
								var user = _users[data.User.Id];
								if (user != null)
									user.UpdateActivity(DateTime.UtcNow);
							}*/
							if (member != null)
							{
								member.Update(data);
								RaiseUserPresenceUpdated(member);
							}
						}
						break;
					case "VOICE_STATE_UPDATE":
						{
							var data = e.Payload.ToObject<Events.VoiceStateUpdate>(_serializer);
							var member = _members[data.UserId, data.GuildId];
							/*if (_config.TrackActivity)
							{
								var user = _users[data.User.Id];
								if (user != null)
									user.UpdateActivity(DateTime.UtcNow);
							}*/
							if (member != null)
							{
								member.Update(data);
								if (member.IsSpeaking)
								{
									member.IsSpeaking = false;
									RaiseUserIsSpeaking(member, false);
								}
								RaiseUserVoiceStateUpdated(member);
							}
						}
						break;
					case "TYPING_START":
						{
							var data = e.Payload.ToObject<Events.TypingStart>(_serializer);
							var channel = _channels[data.ChannelId];
							var user = _users[data.UserId];

							if (user != null)
							{
								if (channel != null)
									RaiseUserIsTyping(user, channel);
							}
							if (_config.TrackActivity)
							{
								if (channel.IsPrivate)
								{
									if (user != null)
										user.UpdateActivity();
								}
								else
								{
									var member = _members[data.UserId, channel.ServerId];
									if (member != null)
										member.UpdateActivity();
								}
							}
						}
						break;

					//Voice
					case "VOICE_SERVER_UPDATE":
						{
							var data = e.Payload.ToObject<Events.VoiceServerUpdate>(_serializer);
							if (data.GuildId == _voiceSocket.CurrentVoiceServerId)
							{
								var server = _servers[data.GuildId];
								if (_config.EnableVoice)
								{
									_voiceSocket.Host = "wss://" + data.Endpoint.Split(':')[0];
									await _voiceSocket.Login(_currentUserId, _dataSocket.SessionId, data.Token, _cancelToken).ConfigureAwait(false);
								}
							}
						}
						break;

					//Settings
					case "USER_UPDATE":
						{
							var data = e.Payload.ToObject<Events.UserUpdate>(_serializer);
							var user = _users[data.Id];
							if (user != null)
							{
								user.Update(data);
								RaiseUserUpdated(user);
							}
						}
						break;
					case "USER_SETTINGS_UPDATE":
						{
							//TODO: Process this
						}
						break;

					//Others
					default:
						RaiseOnLog(LogMessageSeverity.Warning, LogMessageSource.DataWebSocket, $"Unknown message type: {e.Type}");
						break;
				}
			};
		}

		//Connection
		/// <summary> Connects to the Discord server with the provided token. </summary>
		public async Task Connect(string token)
		{
			if (_state != (int)DiscordClientState.Disconnected)
				await Disconnect().ConfigureAwait(false);

			await ConnectInternal(token)
				.Timeout(_config.ConnectionTimeout)
				.ConfigureAwait(false);
		}
		/// <summary> Connects to the Discord server with the provided email and password. </summary>
		/// <returns> Returns a token for future connections. </returns>
		public async Task<string> Connect(string email, string password)
		{
			if (_state != (int)DiscordClientState.Disconnected)
				await Disconnect().ConfigureAwait(false);

			string token;
			try
			{
				var response = await _api.Login(email, password).ConfigureAwait(false);
				token = response.Token;
                if (_config.LogLevel >= LogMessageSeverity.Verbose)
					RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Authentication, "Login successful, got token.");
			}
			catch (TaskCanceledException) { throw new TimeoutException(); }

			return await ConnectInternal(token)
				.Timeout(_config.ConnectionTimeout)
				.ConfigureAwait(false);
		}
		private async Task<string> ConnectInternal(string token)
		{
			try
			{
				_disconnectedEvent.Reset();
				_cancelTokenSource = new CancellationTokenSource();
				_cancelToken = _cancelTokenSource.Token;
				_api.Token = token;
				_api.CancelToken = _cancelToken;
				_token = token;
				_state = (int)DiscordClientState.Connecting;
				
				string url = (await _api.GetWebSocketEndpoint().ConfigureAwait(false)).Url;
				if (_config.LogLevel >= LogMessageSeverity.Verbose)
					RaiseOnLog(LogMessageSeverity.Verbose, LogMessageSource.Authentication, $"Websocket endpoint: {url}");

				_dataSocket.Host = url;
				_dataSocket.ParentCancelToken = _cancelToken;
				await _dataSocket.Login(token).ConfigureAwait(false);				

				_runTask = RunTasks();

				try
				{
					//Cancel if either Disconnect is called, data socket errors or timeout is reached
					var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(_cancelToken, _dataSocket.CancelToken).Token;
					_connectedEvent.Wait(cancelToken);
				}
				catch (OperationCanceledException)
				{
					_dataSocket.ThrowError(); //Throws data socket's internal error if any occured
					throw;
				}

				//_state = (int)DiscordClientState.Connected;
				_token = token;
				return token;
			}
			catch
			{

				await Disconnect().ConfigureAwait(false);
				throw;
			}
		}
		protected void CompleteConnect()
		{
			_state = (int)DiscordClientState.Connected;
			_connectedEvent.Set();
			RaiseConnected();
		}

		/// <summary> Disconnects from the Discord server, canceling any pending requests. </summary>
		public Task Disconnect() => DisconnectInternal(new Exception("Disconnect was requested by user."), isUnexpected: false);
		protected Task DisconnectInternal(Exception ex = null, bool isUnexpected = true, bool skipAwait = false)
		{
			int oldState;
			bool hasWriterLock;

			//If in either connecting or connected state, get a lock by being the first to switch to disconnecting
			oldState = Interlocked.CompareExchange(ref _state, (int)DiscordClientState.Disconnecting, (int)DiscordClientState.Connecting);
			if (oldState == (int)DiscordClientState.Disconnected) return TaskHelper.CompletedTask; //Already disconnected
			hasWriterLock = oldState == (int)DiscordClientState.Connecting; //Caused state change
			if (!hasWriterLock)
			{
				oldState = Interlocked.CompareExchange(ref _state, (int)DiscordClientState.Disconnecting, (int)DiscordClientState.Connected);
				if (oldState == (int)DiscordClientState.Disconnected) return TaskHelper.CompletedTask; //Already disconnected
				hasWriterLock = oldState == (int)DiscordClientState.Connected; //Caused state change
			}

			if (hasWriterLock)
			{
				_wasDisconnectUnexpected = isUnexpected;
				_disconnectReason = ex != null ? ExceptionDispatchInfo.Capture(ex) : null;
				_cancelTokenSource.Cancel();
			}

			if (!skipAwait)
				return _runTask ?? TaskHelper.CompletedTask;
			else
				return TaskHelper.CompletedTask;
		}

		private async Task RunTasks()
		{
			Task task;
			if (_config.UseMessageQueue)
				task = MessageQueueLoop();
			else
				task = _cancelToken.Wait();

			try { await task.ConfigureAwait(false); }
			catch (Exception ex) { await DisconnectInternal(ex, skipAwait: true).ConfigureAwait(false); }

			//When the first task ends, make sure the rest do too
			await DisconnectInternal(skipAwait: true);

			await Cleanup().ConfigureAwait(false);
			_runTask = null;
		}
		private async Task Cleanup()
		{
			var wasDisconnectUnexpected = _wasDisconnectUnexpected;
			_wasDisconnectUnexpected = false;

			await _dataSocket.Disconnect().ConfigureAwait(false);
			if (_config.EnableVoice)
				await _voiceSocket.Disconnect().ConfigureAwait(false);

			if (_config.UseMessageQueue)
			{
				Message ignored;
				while (_pendingMessages.TryDequeue(out ignored)) { }
			}
			
			_channels.Clear();
			_members.Clear();
			_messages.Clear();
			_roles.Clear();
			_servers.Clear();
			_users.Clear();

			_currentUser = null;
			_currentUserId = null;
			_token = null;

			if (!wasDisconnectUnexpected)
			{
				_state = (int)DiscordClientState.Disconnected;
				_disconnectedEvent.Set();
			}
			_connectedEvent.Reset();
		}

		//Helpers
		/// <summary> Blocking call that will not return until client has been stopped. This is mainly intended for use in console applications. </summary>
		public void Block()
		{
			_disconnectedEvent.WaitOne();
		}

		private void CheckReady(bool checkVoice = false)
		{
			switch (_state)
			{
				case (int)DiscordClientState.Disconnecting:
					throw new InvalidOperationException("The client is disconnecting.");
				case (int)DiscordClientState.Disconnected:
					throw new InvalidOperationException("The client is not connected to Discord");
				case (int)DiscordClientState.Connecting:
					throw new InvalidOperationException("The client is connecting.");
			}
			
			if (checkVoice && !_config.EnableVoice)
				throw new InvalidOperationException("Voice is not enabled for this client.");
		}
		private void RaiseEvent(string name, Action action)
		{
			try { action(); }
			catch (Exception ex)
			{
				RaiseOnLog(LogMessageSeverity.Error, LogMessageSource.Client,
					$"{name} event handler raised an exception: ${ex.GetBaseException().Message}");
			}
		}

		//Experimental
		private Task MessageQueueLoop()
		{
			var cancelToken = _cancelToken;
			int interval = _config.MessageQueueInterval;

			return Task.Run(async () =>
			{
				Message msg;
				while (!cancelToken.IsCancellationRequested)
				{
					while (_pendingMessages.TryDequeue(out msg))
					{
						bool hasFailed = false;
						Responses.SendMessage response = null;
						try
						{
							response = await _api.SendMessage(msg.ChannelId, msg.RawText, msg.MentionIds, msg.Nonce, msg.IsTTS).ConfigureAwait(false);
						}
						catch (WebException) { break; }
						catch (HttpException) { hasFailed = true; }

						if (!hasFailed)
						{
							_messages.Remap(msg.Id, response.Id);
							msg.Id = response.Id;
							msg.Update(response);
						}
						msg.IsQueued = false;
						msg.HasFailed = hasFailed;
						RaiseMessageSent(msg);
                    }
					await Task.Delay(interval).ConfigureAwait(false);
				}
			});
		}
		private string GenerateNonce()
		{
			lock (_rand)
				return _rand.Next().ToString();
		}
	}
}
