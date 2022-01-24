using Newtonsoft.Json;
using Oxide.Core;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("BoomBoxStations", "Lpsd", "0.0.1")]
    [Description("Custom radio stream/URL manager")]

    class BoomBoxStations : RustPlugin
    {
        #region Initialization
        private BBStoredData storedData = new BBStoredData();

        private void Init()
        {
            // Add permissions
            Type type = typeof(Permissions);

            foreach (var prop in type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            {
                var obj = prop.GetValue(null);

                if (obj != null)
                {
                    string name = (string)obj;

                    permission.RegisterPermission(name, this);
                }

            }

            LoadData();
            UpdateServerStreams();
        }
        #endregion

        #region Enums
        public enum BBStreamStatus
        {
            PENDING,
            VERIFIED,
            ANY         // helper
        }

        #endregion

        #region Permissions
        private static class Permissions
        {
            public static readonly string addpending         = "boomboxstations.addpending";         // Allows adding streams (set as pending)
            public static readonly string add                = "boomboxstations.add";                // Allows adding streams (set as verified)

            public static readonly string get                = "boomboxstations.get";                // Allows user to retrieve verified streams (listed via cmd)

            public static readonly string getpending         = "boomboxstations.getpending";         // Allows user to retrieve pending streams
            public static readonly string getowner           = "boomboxstations.getowner";           // Allows user to retrieve their own verified streams
            public static readonly string getpendingowner    = "boomboxstations.getpendingowner";    // Allows user to retrieve their own pending streams
            public static readonly string removeowner        = "boomboxstations.removeowner";        // Allows removal of a verified stream, only if they are the owner
            public static readonly string removependingowner = "boomboxstations.removependingowner"; // Allows removal of a pending stream, only if they are the owner
            public static readonly string remove             = "boomboxstations.remove";             // Allows removal of any verified stream
            public static readonly string removepending      = "boomboxstations.removepending";      // Allows removal of any pending stream
            public static readonly string approve            = "boomboxstations.approve";            // Allows approval of a pending stream
            public static readonly string clear              = "boomboxstations.clear";              // Allows clearing of all verified streams
            public static readonly string clearpending       = "boomboxstations.clearpending";       // Allows clearing of all pending streams

            public static readonly string addunlimited       = "boomboxstations.addunlimited";       // Allows user to add as many streams as they want
                                                                                                     // (regardless of `BBStoredData.maxStreamsPerPlayer`)

            public static readonly string setstreamlimit     = "boomboxstations.setstreamlimit";     // Allows user to change `BBStoredData.maxStreamsPerPlayer` via command
            public static readonly string getstreamlimit     = "boomboxstations.getstreamlimit";     // Allows user to get the value of `BBStoredData.maxStreamsPerPlayer` via command
        }
        #endregion

        #region Classes
        class PlayerInfo
        {
            public string Name { get; set; } = string.Empty;
            public string ID { get; set; } = string.Empty;

            public PlayerInfo(string name, string id)
            {
                Name = name;
                ID = id;
            }
        }
        class BBStream
        {
            public int ID { get; set; } = -1;
            public string Name { get; set; } = string.Empty;
            public string URL { get; set; } = string.Empty;
            public BBStreamStatus Status { get; set; }
            public PlayerInfo Owner { get; set; }

            public BBStream(int id, string name, string url, PlayerInfo ownerInfo, BBStreamStatus status = BBStreamStatus.PENDING)
            {
                ID = id;
                Name = name;
                URL = url;
                Status = status;
                Owner = ownerInfo;
            }
        }

        class BBStoredData
        {
            public List<BBStream> streams = new List<BBStream>();

            public int maxStreamsPerPlayer = 3;
        }
        #endregion

        #region Helpers
        private void OutputStreamsToChat(BasePlayer playerToOutput, List<BBStream> streams)
        {
            if (!streams.Any())
            {
                PrintToChat(playerToOutput, "No streams found");
                return;
            }

            int i = 0;
            foreach (BBStream stream in streams)
            {
                PrintToChat(playerToOutput, 
                    String.Format(
                        "[#{0}] Name: {1}, \n" +
                        "URL: {2}, \n" + 
                        "Status: <color=" + (stream.Status == BBStreamStatus.VERIFIED ? "green" : "red") + ">{3}</color>, \n" +
                        "Queued By: {4}", 
                        // args
                        stream.ID, stream.Name, stream.URL, Enum.GetName(typeof(BBStreamStatus), stream.Status), stream.Owner.Name
                    )
                );

                i++;
            }
        }
        private static bool IsValidURL(string s, out Uri resultURI)
        {
            if (!Regex.IsMatch(s, @"^https?:\/\/", RegexOptions.IgnoreCase))
                s = "http://" + s;

            if (Uri.TryCreate(s, UriKind.Absolute, out resultURI))
                return (resultURI.Scheme == Uri.UriSchemeHttp ||
                        resultURI.Scheme == Uri.UriSchemeHttps);

            return false;
        }

        private bool InBounds(string[] array, int index)
        {
            return (index >= 0) && (index < array.Length);
        }

        private int GetUnusedID()
        {
            List<int> ids = new List<int>();

            foreach (BBStream stream in storedData.streams)
            {
                ids.Add(stream.ID);
            }

            if (ids.Count == 0)
                return 0;

            ids.Sort();

            int lastID = 0;
            foreach (int id in ids)
            {
                if (id - lastID > 1)
                    return (lastID + 1);

                lastID = id;
            }

            return lastID + 1;
        }
        #endregion

        #region Data / Storage
        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<BBStoredData>(Title);
            }
            catch
            {
                storedData = new BBStoredData();
                SaveData();
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Title, storedData);
        }

        private void ClearData()
        {
            storedData = new BBStoredData();
            SaveData();
            UpdateServerStreams();
        }
        #endregion

        #region Stream Handling
        private bool AddStream(string name, string url, BasePlayer owner, bool verified = false)
        {
            string ownerID = owner.UserIDString;

            if (!permission.UserHasPermission(ownerID, Permissions.add) && !permission.UserHasPermission(ownerID, Permissions.addpending))
                return false;

            List<BBStream> ownerStreams = GetStreamsByOwnerID(ownerID, BBStreamStatus.ANY);

            if (ownerStreams != null && !permission.UserHasPermission(ownerID, Permissions.addunlimited) && ownerStreams.Count >= storedData.maxStreamsPerPlayer)
            {
                PrintToChat(owner, String.Format("You can only have a maximum of {0} streams pending AND verified at any given time!", storedData.maxStreamsPerPlayer));
                return false;
            }

            if (name.Length > 32)
            {
                PrintToChat(owner, String.Format("Stream name must be <= 32 characters, got {0}", name.Length));
                return false;
            }

            Uri  uriResult;
            bool valid = IsValidURL(url, out uriResult);

            if (!valid)
            {
                PrintToChat(owner, String.Format("Invalid stream URL, got {0}", url));
                return false;
            }

            BBStreamStatus status = (verified) ? BBStreamStatus.VERIFIED : BBStreamStatus.PENDING;
            PlayerInfo     ownerInfo = new PlayerInfo(owner.displayName, owner.UserIDString);
            string         absoluteUri = uriResult.AbsoluteUri;

            storedData.streams.Add(new BBStream(GetUnusedID(), name, absoluteUri, ownerInfo, status));
            PrintToChat(owner, String.Format("Stream added" + (status == BBStreamStatus.VERIFIED ? "" : " (awaiting verification from admin)") + " [{0}, {1}]", name, absoluteUri));

            SaveData();
            UpdateServerStreams();

            return true;
        }

        private bool RemoveStream(BBStream stream)
        {
            int index = storedData.streams.IndexOf(stream);

            if (index == -1)
                return false;

            storedData.streams.RemoveAt(index);

            SaveData();
            UpdateServerStreams();

            return true;
        }

        private bool RemoveStream(int id)
        {
            object stream = GetStreamByID(id);

            if (stream is BBStream)
            {
                BBStream s = (BBStream)stream;
                return RemoveStream(s);
            }

            return false;
        }

        private bool RemoveStream(int id, BasePlayer owner)
        {
            object stream = GetStreamByID(id);

            if (stream is BBStream)
            {
                BBStream s = (BBStream)stream;

                if (s.Owner.ID == owner.UserIDString)
                    return RemoveStream(s);
            }

            PrintToChat(owner, String.Format("Stream not found with ID {0}", id));
            return false;
        }

        private List<BBStream> GetStreamsByName(string name, BBStreamStatus status = BBStreamStatus.VERIFIED)
        {
            List<BBStream> streams = (status == BBStreamStatus.ANY ? storedData.streams : GetStreams(status));
            List<BBStream> streamsWithName = new List<BBStream>();

            foreach (BBStream stream in streams)
            {
                if (stream.Name.ToLower().Contains(name.ToLower()))
                {
                    streamsWithName.Add(stream);
                }
            }

            return streamsWithName;
        }

        private List<BBStream> GetStreamsByName(string name, BasePlayer owner, BBStreamStatus status = BBStreamStatus.VERIFIED)
        {
            List<BBStream> streams = (status == BBStreamStatus.ANY ? storedData.streams : GetStreams(status));
            List<BBStream> streamsWithName = new List<BBStream>();

            foreach (BBStream stream in streams)
            {
                if (stream.Owner.ID == owner.UserIDString && stream.Name.ToLower().Contains(name.ToLower()))
                {
                    streamsWithName.Add(stream);
                }
            }

            return streamsWithName;
        }

        private List<BBStream> GetStreamsByOwnerName(string name, BBStreamStatus status = BBStreamStatus.VERIFIED)
        {
            List<BBStream> streams = (status == BBStreamStatus.ANY ? storedData.streams : GetStreams(status));
            List<BBStream> streamsWithName = new List<BBStream>();

            foreach (BBStream stream in streams)
            {
                if (stream.Owner.Name.ToLower().Contains(name.ToLower()))
                    streamsWithName.Add(stream);
            }

            return streamsWithName;
        }

        private object GetStreamByID(int id)
        {
            foreach (BBStream stream in storedData.streams)
            {
                if (stream.ID == id)
                    return stream;
            }

            return false;
        }

        private List<BBStream> GetStreamsByOwnerID(string id, BBStreamStatus status = BBStreamStatus.VERIFIED)
        {
            List<BBStream> streams = (status == BBStreamStatus.ANY ? storedData.streams : GetStreams(status));
            List<BBStream> streamsWithName = new List<BBStream>();

            foreach (BBStream stream in streams)
            {
                if (stream.Owner.ID == id)
                    streamsWithName.Add(stream);
            }

            return streamsWithName;
        }

        private List<BBStream> GetStreams(BBStreamStatus status)
        {
            if (status == BBStreamStatus.ANY)
                return storedData.streams;

            List<BBStream> streams = new List<BBStream>();

            foreach (BBStream stream in storedData.streams)
            {
                if (stream.Status == status)
                {
                    streams.Add(stream);
                }
            }

            return streams;
        }

        private List<BBStream> GetStreams(BBStreamStatus status, BasePlayer owner)
        {
            if (status == BBStreamStatus.ANY)
                return storedData.streams;

            List<BBStream> streams = new List<BBStream>();

            foreach (BBStream stream in storedData.streams)
            {
                if (owner == null && stream.Status == status)
                {
                    streams.Add(stream);
                }
                else if (owner != null && stream.Owner.ID == owner.UserIDString && stream.Status == status)
                {
                    streams.Add(stream);
                }
            }

            return streams;
        }

        private bool ApproveStream(int id)
        {
            object stream = GetStreamByID(id);

            if (stream is BBStream)
            {
                BBStream s = (BBStream)stream;
                ApproveStream(s);

                return true;
            }

            return false;
        }

        private void ApproveStream(BBStream stream)
        {
            stream.Status = BBStreamStatus.VERIFIED;

            SaveData();
            UpdateServerStreams();
        }

        private void UpdateServerStreams()
        {
            string args = "";

            int i = 0;
            foreach (BBStream stream in storedData.streams)
            {
                if (stream.Status == BBStreamStatus.VERIFIED)
                {
                    args = args + (i == 0 ? "" : ",") + String.Format("{0},{1}", stream.Name, stream.URL);
                    i++;
                }
            }

            // This *should* be safe, `args` is super-duper unsafe client input - however the console should be restricted to only run "BoomBox.ServerUrlList"
            // (they can't just inject a ; or something to start a new command)
            ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "BoomBox.ServerUrlList", args);
        }
        #endregion

        #region Commands
        [ChatCommand("addstream")]
        private void CmdAddStream(BasePlayer player, string cmd, string[] args)
        {
            bool hasAdd = permission.UserHasPermission(player.UserIDString, Permissions.add);
            bool hasAddPending = permission.UserHasPermission(player.UserIDString, Permissions.addpending);

            if (!hasAdd && !hasAddPending)
                return;

            if (args == null || args.Length != 2 || args[0] == String.Empty || args[1] == String.Empty)
                return;

            AddStream(args[0], args[1], player, hasAdd);
        }

        [ChatCommand("approvestream")]
        private void CmdApproveStreamByID(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, Permissions.approve))
                return;

            if (args == null || args.Length == 0 || args[0] == String.Empty)
                return;

            int index;

            if (!Int32.TryParse(args[0], out index))
            {
                PrintToChat(player, String.Format("Invalid stream index provided, got {0}", args[0]));
                return;
            }

            object stream = GetStreamByID(index);

            if (stream is Boolean)
            {
                PrintToChat(player, String.Format("Stream not found at index {0}", args[0]));
                return;
            }
            else if (stream is BBStream)
            {
                BBStream s = (BBStream)stream;
                ApproveStream(s);

                PrintToChat(player, String.Format("Stream ID {0} approved successfully! [Name: {1}, URL: {2}, Queued By: {3}]", index, s.Name, s.URL, s.Owner.Name));
            }
        }

        [ChatCommand("getstreams")]
        private void CmdGetStreams(BasePlayer player, string cmd, string[] args)
        {
            bool hasGet = permission.UserHasPermission(player.UserIDString, Permissions.get);
            bool hasGetOwner = permission.UserHasPermission(player.UserIDString, Permissions.getowner);

            if (!hasGet && !hasGetOwner)
                return;

            string argName = String.Empty;
            string argStatus = String.Empty;

            if (args != null && args.Length >= 0)
            {
                argName = InBounds(args, 0) ? args[0] : "";
                argStatus = InBounds(args, 1) ? args[1] : "";
            }

            var checkOwner = (!hasGet && hasGetOwner) ? player : null;
            BBStreamStatus status;

            switch (argStatus)
            {
                case "verified":
                    status = BBStreamStatus.VERIFIED;
                    break;
                case "pending":
                    status = BBStreamStatus.PENDING;
                    break;
                default:
                    status = BBStreamStatus.VERIFIED;
                    break;
            }

            List<BBStream> streams = new List<BBStream>();
            
            if (argName == String.Empty)
            {
                if (checkOwner != null)
                    streams = GetStreams(status, checkOwner);
                else
                    streams = GetStreams(status);
            }
            else
            {
                if (checkOwner != null)
                    streams = GetStreamsByName(argName, checkOwner, status);
                else
                    streams = GetStreamsByName(argName, status);
            }

            OutputStreamsToChat(player, streams);
        }

        [ChatCommand("getstreamsowner")]
        private void CmdGetStreamsByOwnerName(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, Permissions.get))
                return;

            string argName = String.Empty;
            string argStatus = String.Empty;

            if (args != null && args.Length >= 0)
            {
                argName = InBounds(args, 0) ? args[0] : "";

                if (argName == String.Empty)
                {
                    PrintToChat(player, "Usage: /getstreamsowner <name> <\"verified\" or \"pending\"> \n" + "e.g: /getstreamsowner \"player123\" \"pending\"");
                    return;
                }

                argStatus = InBounds(args, 1) ? args[1] : "";
            }
            else
            {
                PrintToChat(player, "Usage: /getstreamsowner <name> <\"verified\" or \"pending\"> \n" + "e.g: /getstreamsowner \"player123\" \"pending\"");
                return;
            }

            BBStreamStatus status;

            switch (argStatus)
            {
                case "verified":
                    status = BBStreamStatus.VERIFIED;
                    break;
                case "pending":
                    status = BBStreamStatus.PENDING;
                    break;
                default:
                    status = BBStreamStatus.ANY;
                    break;
            }
                
            List<BBStream> streams = GetStreamsByOwnerName(argName, status);
            OutputStreamsToChat(player, streams);
        }

        [ChatCommand("getstreamsownerid")]
        private void CmdGetStreamsByOwnerID(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, Permissions.get))
                return;

            string argID = String.Empty;
            string argStatus = String.Empty;

            if (args != null && args.Length >= 0)
            {
                argID = InBounds(args, 0) ? args[0] : "";

                if (argID == String.Empty)
                {
                    PrintToChat(player, "Usage: /getstreamsownerid <id> <\"verified\" or \"pending\"> \n" + "e.g: /getstreamsownerid \"76561197960287930\" \"verified\"");
                    return;
                }

                argStatus = InBounds(args, 1) ? args[1] : "";
            }
            else
            {
                PrintToChat(player, "Usage: /getstreamsownerid <id> <\"verified\" or \"pending\"> \n" + "e.g: /getstreamsownerid \"76561197960287930\" \"verified\"");
                return;
            }

            BBStreamStatus status;

            switch (argStatus)
            {
                case "verified":
                    status = BBStreamStatus.VERIFIED;
                    break;
                case "pending":
                    status = BBStreamStatus.PENDING;
                    break;
                default:
                    status = BBStreamStatus.ANY;
                    break;
            }

            List<BBStream> streams = GetStreamsByOwnerID(argID, status);
            OutputStreamsToChat(player, streams);
        }

        [ChatCommand("removestream")]
        private void CmdRemoveStreamByID(BasePlayer player, string cmd, string[] args)
        {
            bool hasRemove = permission.UserHasPermission(player.UserIDString, Permissions.remove);
            bool hasRemoveOwner = permission.UserHasPermission(player.UserIDString, Permissions.removeowner);

            if (!hasRemove && !hasRemoveOwner)
                return;

            string argID = String.Empty;

            if (args != null && args.Length >= 0)
            {
                argID = InBounds(args, 0) ? args[0] : "";

                if (argID == String.Empty)
                {
                    PrintToChat(player, "Usage: /removestream <id> \n" + "e.g: /removestream \"2\"");
                    return;
                }
            }
            else
            {
                PrintToChat(player, "Usage: /removestream <id> \n" + "e.g: /removestream \"2\"");
                return;
            }

            var checkOwner = (!hasRemove && hasRemoveOwner) ? player : null;
            int index;

            if (!Int32.TryParse(argID, out index))
            {
                PrintToChat(player, String.Format("Invalid stream ID provided, got {0}", argID));
                return;
            }
            
            if (checkOwner != null && !RemoveStream(index, checkOwner))
            {
                return;
            }
            else if (checkOwner == null && !RemoveStream(index))
            {
                PrintToChat(player, String.Format("Stream with ID {0} not found", index));
                return;
            }

            PrintToChat(player, String.Format("Successfully removed stream with ID {0}", index));
        }

        [ChatCommand("clearstreams")]
        void CmdClearStreams(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, Permissions.clear))
                return;

            string argStatus = String.Empty;

            if (args != null && args.Length >= 0)
            {
                argStatus = InBounds(args, 0) ? args[0] : "";
            }
            else
            {
                PrintToChat(player, "Usage: /getstreamsownerid <id> <\"verified\" or \"pending\"> \n" + "e.g: /getstreamsownerid \"76561197960287930\" \"verified\"");
                return;
            }

            BBStreamStatus status;

            switch (argStatus)
            {
                case "verified":
                    status = BBStreamStatus.VERIFIED;
                    break;
                case "pending":
                    status = BBStreamStatus.PENDING;
                    break;
                default:
                    status = BBStreamStatus.ANY;
                    break;
            }

            if (status == BBStreamStatus.ANY)
                storedData.streams.Clear();
            else
                storedData.streams.RemoveAll(stream => stream.Status == status);

            SaveData();
            UpdateServerStreams();

            PrintToChat(player, "All " + ((status == BBStreamStatus.ANY) ? "" : (argStatus + " ")) + "streams removed successfully");
        }

        [ChatCommand("setstreamlimit")]
        void CmdSetPlayerStreamLimit(BasePlayer player, string cmd, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, Permissions.setstreamlimit))
                return;

            string argLimit = String.Empty;

            if (args != null && args.Length >= 0)
            {
                argLimit = InBounds(args, 0) ? args[0] : "";

                if (argLimit == String.Empty)
                {
                    PrintToChat(player, "Usage: /setplayerstreamlimit <limit> \n" + "e.g: /setplayerstreamlimit 3");
                    return;
                }
            }
            else
            {
                PrintToChat(player, "Usage: /setplayerstreamlimit <limit> \n" + "e.g: /setplayerstreamlimit 3");
                return;
            }

            int limit;

            if (!Int32.TryParse(argLimit, out limit) || limit < 0)
            {
                PrintToChat(player, String.Format("Invalid limit provided, got {0} (must be a positive integer)", argLimit));
                return;
            }

            storedData.maxStreamsPerPlayer = limit;
            SaveData();

            PrintToChat(player, String.Format("Max streams per player set to {0}", limit));
        }

        [ChatCommand("getstreamlimit")]
        void CmdGetPlayerStreamLimit(BasePlayer player)
        {
            if (!permission.UserHasPermission(player.UserIDString, Permissions.getstreamlimit))
                return;

            PrintToChat(String.Format("Max streams per player: {0}", storedData.maxStreamsPerPlayer.ToString()));
        }
        #endregion

        #region Commands Info / Usage
        // Command: addstream <name> <url>
        // Description: Adds a stream with the given name and URL.
        // Permissions: add, addpending
        // Required Arguments: <name>, <url>

        // Command: approvestream <id>
        // Description: Approves a stream with the specified ID.
        // Permissions: approve
        // Required Arguments: <id>

        // Command: getstreams <name> <status>
        // Description: Gets all or any streams with optional name and status arguments.
        // Permissions: get, getowner
        // Required Arguments: n/a

        // Command: getstreamsowner <owner-name> <status>
        // Description: Gets streams by owner name with optional status argument (default = all streams returned, regardless of status)
        // Permissions: get
        // Required Arguments: <owner-name>

        // Command: getstreamsownerid <owner-id> <status>
        // Description: Gets streams by owner ID with optional status argument (default = all streams returned, regardless of status)
        // Permissions: get
        // Required Arguments: <owner-id>

        // Command: removestream <id>
        // Description: Remove a stream by ID
        // Permissions: remove, removepending, removeowner, removependingowner
        // Required Arguments: <id>

        // Command: clearstreams <status>
        // Description: Clears all streams by status ("verified" or "pending"), if status isn't provided ALL streams are cleared
        // Permissions: remove, removepending
        // Required Arguments: n/a

        // Command: setstreamlimit <limit>
        // Description: Sets the maximum amount of streams players can add (verified and pending combined).
        // Permissions: setstreamlimit
        // Permission override: addunlimited (allows a user to bypass max stream limits)
        // Required Arguments: <limit>

        // Command: getstreamlimit 
        // Description: Gets the current `BBStoredData.maxStreamsPerPlayer` value
        // Permissions: getstreamlimit
        // Required Arguments: n/a
        #endregion
    }
}