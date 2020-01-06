using System;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Network;

// TODO
// find point to check that it is a server - this.Helper.Events.GameLaunched?
// document that newPlayerAuth config only active if playerAuth is active
// is confirmation of new auth really needed? currently this is not processed.
// - make sure no duplicate entries are created in the databases

namespace AuthDewServer
{
    class ModConfig
    {
        public bool playerAuth { get; set; } = true; //
        //public bool newPlayerAuth { get; set; } = false; // set server to authenticate players that join
        public bool forcePlayerAuthCreation { get; set; } = true; // any player that doesn't have an auth code but has the Mod will be given one; otherwise only upon request
        public string serverInviteCode { get; set; } = ""; // the invite code itself, TODO: mention max string length
        public bool verboseLog { get; set; } = false; // verbose log to console for development/debugging
    }

    // from github.com/funny-snek/anticheat-and-servercode
    internal class PlayerSlot
    {
        /// <summary>The metadata for this player.</summary>
        public IMultiplayerPeer Peer { get; set; }

        /// <summary>The number of seconds until the player should be kicked.</summary>
        public int CountDownSeconds { get; set; }
    }

    class AuthDewMessage
    {
        public int messageApiMajor { get; set; } = 1;
        public int messageApiMinor { get; set; } = 0;
        public int messageApiPatch { get; set; } = 0;
        public string messageSenderId { get; set; }
        public string messageBody { get; set; }
    }

    public class ModEntry : Mod
    {
        private ModConfig Config;
        string senderId;

        private readonly int SecondsUntilKick = 5;
        private readonly int SecondsForAuth = 20;
        private readonly int SecondsForInviteCode = 20;

        Dictionary<string, string> serverAuthTable; // <farmerName, key>
        Dictionary<long, KeyValuePair<string, string>> unconfirmedNewAuths; // <playerID, <farmerName, passCode>>

        private readonly List<PlayerSlot> PlayersToKick = new List<PlayerSlot>();
        private readonly List<PlayerSlot> PlayersNeedAuth = new List<PlayerSlot>();
        private readonly List<PlayerSlot> PlayersNeedInvite = new List<PlayerSlot>();
        private readonly List<IMultiplayerPeer> NewPlayers = new List<IMultiplayerPeer>();
        List<IMultiplayerPeer> ToRemoveFromNewPlayers = new List<IMultiplayerPeer>();

        bool isServer;
        bool ApiCompatibleBool;

        public override void Entry(IModHelper helper)
        {
            this.Config = this.Helper.ReadConfig<ModConfig>();

            helper.Events.Multiplayer.PeerContextReceived += this.OnPeerContextReceived;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
            helper.Events.GameLoop.Saved += this.OnSaved;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            serverAuthTable = this.Helper.Data.ReadJsonFile<Dictionary<string, string>>("serverAuthTable.json") ?? new Dictionary<string, string>();
            unconfirmedNewAuths = new Dictionary<long,KeyValuePair<string, string>>();

            // check if a senderId already exists, if not create one
            if (this.Helper.Data.ReadJsonFile<string>("senderId.json") == null)
            {
                senderId = createNewRandomString();
                this.Helper.Data.WriteJsonFile<string>("senderId.json", senderId);
            }
            else
            {
                senderId = this.Helper.Data.ReadJsonFile<string>("senderId.json");
            }
            isServer = true; // assumed to be true, set to false later if proven otherwise
            ApiCompatibleBool = true; // assumed to be true, set to false later if proven otherwise

            // TODO: log current configuration to console (inviteCode, playerAuth, verboseLog, forcePlayerAuthCreation)
        }

        private void OnPeerContextReceived(object sender, PeerContextReceivedEventArgs e)
        {
            // if player isn't host, just return
            if (!CheckIfServer())
                return;

            // add to the NewPlayers list to process once online
            NewPlayers.Add(e.Peer);
        }

        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            // if player isn't host, just return
            // TODO: uncomment or implement otherwise
            if (!CheckIfServer())
                return;

            // HOST: receive authCode, inviteCode, confirmation of received authCode
            // DEBUG/DEVELOPMENT STUFF - TODO: remove when testing done.
            this.Monitor.Log($"received message of type {e.Type} by mod {e.FromModID} from " +
                $"player {e.FromPlayerID}, is the player host? " +
                $"{this.Helper.Multiplayer.GetConnectedPlayer(e.FromPlayerID).IsHost}");
            this.Monitor.Log($"comparing remote mod ID {e.FromModID} to this.ModManifest.UniqueID " +
                $"{this.ModManifest.UniqueID}");

            AuthDewMessage message = e.ReadAs<AuthDewMessage>();

            // TODO: make this check optional to allow other server software to interact with the API
            if (e.FromModID != "thfr.AuthDew")
            {
                this.Monitor.Log($"received message from a mod that's not thfr.AuthDew: {e.FromModID}");
                return;
            }

            if (!IsApiCompatible(message))
            {
                if (ApiCompatibleBool)
                {
                    this.Monitor.Log($"ERROR: received message with incompatible API version " +
                        $"{message.messageApiMajor}." +
                        $"{message.messageApiMinor}." +
                        $"{message.messageApiPatch}");
                    ApiCompatibleBool = false;
                }

                // send message to sending player indicating API incompatibility
                SendModMessage("", "ApiIncompatible", "thfr.AuthDew", e.FromPlayerID);
                return;
            }

            isServer = true; // this one may be redundant
            ApiCompatibleBool = true;

            string farmerName = Game1.getFarmer(e.FromPlayerID).Name;
            PlayerSlot slotToRemove = null;

            switch (e.Type)
            {
                case "authResponse":
                    this.Monitor.Log($"received authResponse from {e.FromPlayerID} " +
                        $"(Farmer {farmerName} " +
                        $"with text {message.messageBody}");
                    // player should exist in serverAuthTable
                    // compare message.messageBody to entry in serverAuthTable
                    // if correct, remove e.FromPlayerID from the needAuthList
                    // if incorrect, kick player
                    foreach (PlayerSlot slot in PlayersNeedInvite)
                    {
                        if (slot.Peer.PlayerID == e.FromPlayerID)
                        {
                            slotToRemove = slot;
                        }
                    }
                    if (serverAuthTable[farmerName] == message.messageBody)
                    {
                        // nothing
                    }
                    else
                    {
                        SendDirectMessage(e.FromPlayerID, "Authentication response did not match. Disconnecting...");
                        //KickPlayer(e.FromPlayerID);
                        this.PlayersToKick.Add(new PlayerSlot
                        {
                            Peer = slotToRemove.Peer,
                            CountDownSeconds = this.SecondsUntilKick
                        });
                    }
                    this.Monitor.Log($"removing player {e.FromPlayerID} " +
                        $"(Farmer {farmerName} from the PlayersNeedAuth list");
                    PlayersNeedAuth.Remove(slotToRemove);
                    break;
                case "inviteCodeResponse":
                    this.Monitor.Log($"received inviteCodeResponse from {e.FromPlayerID} " +
                        $"with text {message.messageBody}");
                    // player should exist in inviteCodeTable
                    // if exists, compare e.ReadAs<MPAuthModMessage>().messageBody to entry in inviteCodeTable
                    // if correct, remove e.FromPlayerID from the needInviteCodeList
                    // if incorrect, kick player
                    foreach (PlayerSlot slot in PlayersNeedInvite)
                    {
                        if (slot.Peer.PlayerID == e.FromPlayerID)
                        {
                            slotToRemove = slot;
                        }
                    }
                    if (this.Config.serverInviteCode == message.messageBody)
                    {
                        //nothing
                    }
                    else
                    {
                        SendDirectMessage(e.FromPlayerID, "Invite Code did not match. Disconnecting...");
                        this.PlayersToKick.Add(new PlayerSlot
                        {
                            Peer = slotToRemove.Peer,
                            CountDownSeconds = this.SecondsUntilKick
                        });
                    }
                    this.Monitor.Log($"removing player {e.FromPlayerID} " +
                        $"(Farmer {farmerName} from the PlayersNeedInvite list");
                    PlayersNeedInvite.Remove(slotToRemove);
                    break;
                case "confirmNewAuth":
                    this.Monitor.Log($"received confirmNewAuth from {e.FromPlayerID} " +
                        $"with text {message.messageBody}");
                    // Check #1: is e.FromPlayerID in the the unconfirmedAuths?
                    if (!unconfirmedNewAuths.ContainsKey(e.FromPlayerID))
                    {
                        this.Monitor.Log($"ERROR: received confirmNewAuth message from player {e.FromPlayerID} " +
                        	"who is not in unconfirmedNewAuths");
                        break;
                    }
                    // TODO: This should only be done if the farmerName has been finalized. The onus for this
                    // may need to be on the client mod.
                    // Check #2: Does the name provided in messageBody match the entry in unconfirmedAuths?
                    if (!(unconfirmedNewAuths[e.FromPlayerID].Key == message.messageBody))
                    {
                        this.Monitor.Log($"ERROR: received confirmNewAuth message from player " +
                        	$"{e.FromPlayerID} with name mismatch (" +
                        		$"server: {unconfirmedNewAuths[e.FromPlayerID].Key}, " +
                        		$"client message: {message.messageBody}");
                        break;
                    }
                    // If checks #1 and #2 pass, add to serverAuthTable
                    serverAuthTable.Add(unconfirmedNewAuths[e.FromPlayerID].Key, 
                        unconfirmedNewAuths[e.FromPlayerID].Value);
                    unconfirmedNewAuths.Remove(e.FromPlayerID);
                    break;
                case "ApiIncompatible":
                    this.Monitor.Log($"WARNING: received ApiIncompatible message from {e.FromPlayerID} with " +
                    	$"message body {message.messageBody}");
                    // TODO: add handler for "ApiIncompatible" message type
                    break;
                default:
                    this.Monitor.Log($"received message of unknown type from {e.FromPlayerID}");
                    break;
            }
        }

        // from github.com/funny-snek/anticheat-and-servercode
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.PlayersToKick.Clear();
            this.PlayersNeedAuth.Clear();
            this.PlayersNeedInvite.Clear();
        }

        private void KickPlayer(long playerID)
        {
            this.Monitor.Log($"kicking player {playerID}");
            try
            {
                Game1.server.sendMessage(playerID, new OutgoingMessage(Multiplayer.disconnecting, playerID));
            }
            catch { /* ignore error if we can't connect to the player */ }
            Game1.server.playerDisconnected(playerID);
            Game1.otherFarmers.Remove(playerID);
        }

        // from github.com/funny-snek/anticheat-and-servercode
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsMainPlayer || !Context.IsWorldReady || !e.IsOneSecond)
                return;

            if (this.Config.verboseLog)
            {
                // log length of all lists to catch any leaks
                this.Monitor.Log($"Counts: " +
                	$"serverAuthTable: {serverAuthTable.Count}, " +
            		$"unconfirmedNewAuths: {unconfirmedNewAuths.Count}, " +
        			$"NewPlayers: {NewPlayers.Count}, " +
    				$"ToRemoveFromNewPlayers: {ToRemoveFromNewPlayers.Count}, " +
    				$"PlayersToKick: {PlayersToKick.Count}, " +
    				$"PlayersNeedAuth: {PlayersNeedAuth.Count}, " +
    				$"PlayersNeedInvite: {PlayersNeedInvite.Count}");
            }

            ProcessNewPlayers();

            // process PlayersNeedAuth
            foreach (PlayerSlot slot in PlayersNeedAuth)
            {
                slot.CountDownSeconds--;
                this.Monitor.Log($"{slot.CountDownSeconds} seconds left to receive authResponse from player {slot.Peer.PlayerID}");
                if (slot.CountDownSeconds < 0)
                {
                    SendDirectMessage(slot.Peer.PlayerID, "Time for authentication expired. Disconnecting...");
                    this.PlayersToKick.Add(new PlayerSlot
                    {
                        Peer = slot.Peer,
                        CountDownSeconds = this.SecondsUntilKick
                    });
                }
            }
            PlayersNeedAuth.RemoveAll(p => p.CountDownSeconds < 0);

            // process PlayersNeedInvite
            foreach (PlayerSlot slot in PlayersNeedInvite)
            {
                slot.CountDownSeconds--;
                this.Monitor.Log($"{slot.CountDownSeconds} seconds left to receive inviteCodeResponse from player {slot.Peer.PlayerID}");
                if (slot.CountDownSeconds < 0)
                {
                    SendDirectMessage(slot.Peer.PlayerID, "Time for invite code expired. Disconnecting...");
                    this.PlayersToKick.Add(new PlayerSlot
                    {
                        Peer = slot.Peer,
                        CountDownSeconds = this.SecondsUntilKick
                    });
                }
            }
            PlayersNeedInvite.RemoveAll(p => p.CountDownSeconds < 0);

            // kick players whose countdowns expired
            foreach (PlayerSlot slot in PlayersToKick)
            {
                slot.CountDownSeconds--;
                if (slot.CountDownSeconds < 0)
                {
                    // get player info
                    long playerID = slot.Peer.PlayerID;
                    string name = Game1.getOnlineFarmers().FirstOrDefault(p => p.UniqueMultiplayerID == slot.Peer.PlayerID)?.Name ?? slot.Peer.PlayerID.ToString();

                    // send chat messages
                    this.SendDirectMessage(playerID, "You're being kicked because you failed to authenticate (MPAuthMod)");

                    // kick player
                    this.KickPlayer(playerID);
                }
            }
            PlayersToKick.RemoveAll(p => p.CountDownSeconds < 0);
        }

        private bool IsApiCompatible(AuthDewMessage message)
        {
            if (message.messageApiMajor >= 1 &&
                message.messageApiMinor >= 0 &&
                message.messageApiPatch >= 0)
            {
                return true;
            }
            else
                return false;
        }

        private void SendModMessage(string messageText, string messageType, string receiverModID, long receiverID)
        {
            AuthDewMessage message = new AuthDewMessage();
            message.messageSenderId = senderId;
            message.messageBody = messageText;
            this.Helper.Multiplayer.SendMessage<AuthDewMessage>(message, messageType,
                modIDs: new[] { receiverModID },
                playerIDs: new[] { receiverID });
            this.Monitor.Log($"sent message of type {messageType}, with senderID {message.messageSenderId}" +
            	$"and text |{message.messageBody}| to player {receiverID}");
        }

        // from github.com/funny-snek/anticheat-and-servercode
        private void SendDirectMessage(long playerID, string text)
        {
            Game1.server.sendMessage(playerID, Multiplayer.chatMessage, Game1.player, this.Helper.Content.CurrentLocaleConstant, text);
        }

        string createNewRandomString()
        {
            // from www.dotnetperls.com/rngcryptoserviceprovider
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            byte[] data = new byte[32];
            rng.GetBytes(data); // fill data buffer with random values
            return BitConverter.ToString(data);
        }

        private bool CheckIfServer()
        {
            if (!Context.IsMainPlayer)
            {
                if (isServer)
                {
                    this.Monitor.Log("WARNING: Player is not main player, AuthDew-server functionality disabled.");
                    isServer = false;
                }
            }
            else
            {
                isServer = true;
            }
            return isServer;
        }

        private void CreateNewAuth(long playerid)
        {
            this.Monitor.Log($"creating new auth code for player {playerid}, " +
            	$"farmer {Game1.getFarmer(playerid)}");
            string newAuthCode = createNewRandomString();
            SendModMessage(newAuthCode, "createNewAuth", "thfr.AuthDew", playerid);
            unconfirmedNewAuths.Add(playerid,
                new KeyValuePair<string, string>(Game1.getFarmer(playerid).Name, newAuthCode));
        }

        // Everything is only saved to file when the game is saved
        // This ensures that registered players don't disappear from the game.
        private void OnSaved(object sender, SavedEventArgs e)
        {
            this.Helper.Data.WriteJsonFile<Dictionary<string, string>>("serverAuthTable.json",
                        serverAuthTable);
        }

        private void ProcessNewPlayers()
        {
            foreach (IMultiplayerPeer playerID in ToRemoveFromNewPlayers)
            {
                NewPlayers.Remove(playerID);
            }
            ToRemoveFromNewPlayers.Clear();
            foreach (IMultiplayerPeer player in NewPlayers)
            {
                string newFarmer = Game1.getOnlineFarmers().FirstOrDefault(p => p.UniqueMultiplayerID == player.PlayerID)?.Name;
                this.Monitor.Log($"lenght of NewFarmer string for {player.PlayerID}: {newFarmer.Length}");
                if (newFarmer != null && newFarmer.Length > 0)
                {
                    this.Monitor.Log($"Found farmer {newFarmer} for player {player.PlayerID}");
                    bool hasAuthDewMod = (player.HasSmapi && (player.GetMod("thfr.AuthDew") != null));

                    // LOGIC OUTLINE:
                    // ==============
                    // check if player joined as a farmer in serverAuthTable
                    // - if so, needs authentication (needAuth = true)
                    //   -- if player has AuthDew client, request authCode and add to PlayersNeedAuth and remove from NewPlayers
                    //   -- if player doesn't have AuthDew client, message & kick & remove from NewPlayers
                    // - if farmer is _not_ in serverAuthTable
                    //   -- if player has AuthDew client
                    //      --- if serverInviteCode is set (Length > 0), send inviteCodeRequest, add to PlayersNeedInviteCode,
                    //          and remove from NewPlayers
                    //          ---- if forcePlayerAuthCreation, handler will have to CreateNewAuth after invite code confirmed
                    //      --- if no serverInviteCode is set
                    //          ---- if forcePlayerAuthCreation, run CreateNewAuth & remove from NewPlayers
                    //          ---- otherwise remove from NewPlayers
                    //   -- if player doesn't have AuthDew client mod
                    //      --- if serverInviteCode is set (Length > 0), message & kick & remove from NewPlayers
                    //      --- otherwise add to ToRemoveFromNewPlayers and return

                    if (serverAuthTable.ContainsKey(newFarmer)) // check if player joined as a farmer in serverAuthTable
                    {
                        if (hasAuthDewMod) // request authCode and add to PlayersNeedAuth
                        {
                            this.Monitor.Log($"Requesting authCode from player {player.PlayerID}");
                            SendModMessage(newFarmer, "authRequest", "thfr.AuthDew", player.PlayerID);
                            this.PlayersNeedAuth.Add(new PlayerSlot
                            {
                                Peer = player,
                                CountDownSeconds = this.SecondsForAuth
                            });
                        }
                        else // if player doesn't have AuthDew client, message & kick
                        {
                            this.Monitor.Log($"Player {player.PlayerID} " +
                            	$"(farmer {newFarmer}) is unable to provide required authentication " +
                            		"because the AuthDew client mod is not installed.");
                            SendDirectMessage(player.PlayerID, $"You need to be authenticated with AuthDew mod to join as player {newFarmer}. Disconnecting...");
                            this.PlayersToKick.Add(new PlayerSlot
                            {
                                Peer = player,
                                CountDownSeconds = this.SecondsUntilKick
                            });
                        }
                    }
                    else // farmer is _not_ in serverAuthTable
                    {
                        if (hasAuthDewMod) // send inviteCode or create new player auth if those are set (forced) in Config
                        {
                            if (this.Config.serverInviteCode.Length > 0) //send inviteCodeRequest, add to PlayersNeedInviteCode
                            {
                                this.Monitor.Log($"requesting invite code from player {player.PlayerID} " +
                                	$"(farmer {newFarmer}");
                                SendModMessage("", "inviteCodeRequest", "thfr.AuthDew", player.PlayerID);
                                this.PlayersNeedInvite.Add(new PlayerSlot
                                {
                                    Peer = player,
                                    CountDownSeconds = this.SecondsForInviteCode
                                });
                            }
                            else // create new auth or do nothing (player will then just be removed from the list and can play)
                            {
                                if (this.Config.forcePlayerAuthCreation) // run CreateNewAuth
                                {
                                    this.Monitor.Log($"forcing auth creation because of settings for player {player.PlayerID}");
                                    CreateNewAuth(player.PlayerID);
                                }
                            }
                        }
                        else // if invite code is set, this player needs to be kicked because of missing AuthDew mod
                        {
                            if (this.Config.serverInviteCode.Length > 0)
                            {
                                this.Monitor.Log($"Player {player.PlayerID} " +
                                    $"(farmer {newFarmer}) is unable to provide required invite code " +
                                        "because the AuthDew client mod is not installed.");
                                SendDirectMessage(player.PlayerID, $"You need to provide the server invite code with AuthDew mod to join as player {newFarmer}. Disconnecting...");
                                this.PlayersToKick.Add(new PlayerSlot
                                {
                                    Peer = player,
                                    CountDownSeconds = this.SecondsUntilKick
                                });
                            }
                            else
                            {
                                this.Monitor.Log($"player {player.PlayerID} doesn't have AuthDew mod; nothing to do.");
                            }
                        }
                    }
                    ToRemoveFromNewPlayers.Add(player); // all players should be processed now, so schedule for removal from NewPlayers

                    // check if player has AuthDew client mod
                    // - if not and serverInviteCode is empty, check if requestde
                    //   -- schedule for removal from NewPlayer
                    // - if not and serverInviteCode is set, send message about needed mod + invite code and kick

                    //if (!hasAuthDewMod && this.Config.serverInviteCode.Length == 0)
                    //{
                    //    this.Monitor.Log($"New player {player.PlayerID} (farmer {newFarmer}")
                    //}

                    //bool needAuth = true;

                    //if (!this.Config.playerAuth)
                    //{
                    //    needAuth = false;
                    //}

                    //if (!this.serverAuthTable.ContainsKey(newFarmer) && !this.Config.forcePlayerAuthCreation)
                    //    needAuth = false;

                    //// TESTING CODE; TODO: remove after testing
                    //this.Monitor.Log($"Length of invite code: {this.Config.serverInviteCode.Length.ToString()}");
                    //bool needInvite = !needAuth && (this.Config.serverInviteCode.Length > 0);

                    //this.Monitor.Log($"Processing new player {player.PlayerID}: needAuth {needAuth}, needInvite {needInvite}");

                    //// new players get a pass unless an invite code is needed
                    //if ((!needAuth && !needInvite) || (!serverAuthTable.ContainsKey(newFarmer)))
                    //{
                    //    //nothing to be done for this player
                    //    this.Monitor.Log($"No authentication needed for player {player.PlayerID}");

                    //    if (this.Config.forcePlayerAuthCreation && hasAuthDewMod)
                    //    {
                    //        //this.Monitor.Log($"creating auth for player {player.PlayerID} " +
                    //        //    $"(Farmer {newFarmer})");
                    //        //string newAuthCode = createNewRandomString();
                    //        //SendModMessage(newAuthCode, "createNewAuth", "thfr.AuthDew", player.PlayerID);
                    //        //serverAuthTable.Add(newFarmer, newAuthCode);
                    //        //this.Helper.Data.WriteJsonFile<Dictionary<string, string>>("serverAuthTable.json",
                    //            //serverAuthTable);
                    //    }
                    //    ToRemoveFromNewPlayers.Add(player);
                    //    return;
                    //}


                    //if (!hasAuthDewMod)
                    //{
                    //    if (needInvite || needAuth)
                    //    {
                    //        this.Monitor.Log($"New peer {player.PlayerID} can't provide invite code or auth code because AuthDew is not available - kicking...");
                    //        SendDirectMessage(player.PlayerID, "You need the AuthDew mod and a valid Invite or Code to join the game! Disconnecting");
                    //        // from github.com/funny-snek/anticheat-and-servercode
                    //        this.PlayersToKick.Add(new PlayerSlot
                    //        {
                    //            Peer = player,
                    //            CountDownSeconds = this.SecondsUntilKick
                    //        });
                    //    }
                    //    else
                    //    {
                    //        // no invite or auth code required
                    //        this.Monitor.Log($"New peer {player.PlayerID} doesn't have AuthDew mod, but it's not needed.");
                    //        ToRemoveFromNewPlayers.Add(player);
                    //        return;
                    //    }
                    //}

                    //if (needAuth)
                    //{
                    //    // send authRequest to newPlayer's AuthDew mod
                    //    this.Monitor.Log($"sending auth request to {player.PlayerID}");
                    //    SendModMessage("", "authRequest", "thfr.AuthDew", player.PlayerID);
                    //    //MPAuthModMessage needAuthMessage = new MPAuthModMessage(MPAuthModMessageType.AUTH_REQUEST, "");
                    //    //this.Helper.Multiplayer.SendMessage(needAuthMessage, "authRequest");
                    //    this.PlayersNeedAuth.Add(new PlayerSlot
                    //    {
                    //        Peer = player,
                    //        CountDownSeconds = this.SecondsForAuth
                    //    });
                    //}
                    //else if (needInvite)
                    //{
                    //    // request inviteCode from newPlayer's AuthDew mod
                    //    this.Monitor.Log($"sending invite code request to {player.PlayerID}");
                    //    SendModMessage("", "inviteCodeRequest", "thfr.AuthDew", player.PlayerID);
                    //    //MPAuthModMessage needInviteCodeMessage = new MPAuthModMessage(MPAuthModMessageType.INVITE_CODE_REQUEST, "");
                    //    //this.Helper.Multiplayer.SendMessage(needInviteCodeMessage, "inviteCodeRequest");
                    //    this.PlayersNeedInvite.Add(new PlayerSlot
                    //    {
                    //        Peer = player,
                    //        CountDownSeconds = SecondsForInviteCode
                    //    });
                    //}
                    //ToRemoveFromNewPlayers.Add(player);
                }
            }
        }
    }
}