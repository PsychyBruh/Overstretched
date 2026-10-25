using Unity.Collections;
using Unity.Netcode;

namespace Overstretched
{
    /// <summary>
    /// Menu actions that every client has to apply itself (revives, teleports, item moves, scaling, camera
    /// shakes...), sent as NGO named messages like StretchedNet. Every message starts with a type byte.
    /// </summary>
    public static class MenuNet
    {
        public const string Channel = "Overstretched.Menu";

        public enum Msg : byte
        {
            Revive = 1,      // host -> all
            Heal,            // host -> all: int player index (-1 = everyone)
            TeleportPlayer,  // host -> all: int player index, Vector3 position, bool inside
            ItemsToShip,     // host -> all: int count, then per item ulong objectId, Vector3 world position
            Scale,           // host -> all: ulong enemy objectId, float scale
            Doors,           // host -> all: byte DoorAction
            Quake,           // host -> all: float seconds
            Scatter,         // host -> all
            LowGravity,      // host -> all: float seconds
            Invisible,       // client -> host and host -> all: int player index, bool on
        }

        public enum DoorAction : byte { Open, Close, Unlock, Lock }

        public delegate void Body(ref FastBufferWriter writer);

        private static CustomMessagingManager registeredOn;

        public static void EnsureRegistered()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || registeredOn == nm.CustomMessagingManager) return;
            nm.CustomMessagingManager.RegisterNamedMessageHandler(Channel, OnMessage);
            registeredOn = nm.CustomMessagingManager;
        }

        private static void OnMessage(ulong sender, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte type);
            Deliver(sender, (Msg)type, reader);
        }

        private static void Deliver(ulong sender, Msg type, FastBufferReader reader)
        {
            try { Features.Receive(sender, type, reader); }
            catch (System.Exception e) { Plugin.Log.LogError($"Menu message {type} failed: {e}"); }
        }

        /// <summary>Host only: deliver to every client, including the host itself.</summary>
        public static void ToAll(Msg type, Body body = null)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            EnsureRegistered();
            var writer = Write(type, body);
            try
            {
                foreach (var id in nm.ConnectedClientsIds)
                    if (id != NetworkManager.ServerClientId)
                        nm.CustomMessagingManager.SendNamedMessage(Channel, id, writer, NetworkDelivery.ReliableFragmentedSequenced);
                DeliverLocally(type, writer);
            }
            finally { writer.Dispose(); }
        }

        /// <summary>Any client: tell the host (the host just handles it directly).</summary>
        public static void ToHost(Msg type, Body body = null)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return;
            EnsureRegistered();
            var writer = Write(type, body);
            try
            {
                if (nm.IsServer) DeliverLocally(type, writer);
                else nm.CustomMessagingManager.SendNamedMessage(Channel, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            finally { writer.Dispose(); }
        }

        private static FastBufferWriter Write(Msg type, Body body)
        {
            var writer = new FastBufferWriter(64, Allocator.Temp, 64 * 1024);
            writer.WriteValueSafe((byte)type);
            body?.Invoke(ref writer);
            return writer;
        }

        private static void DeliverLocally(Msg type, FastBufferWriter writer)
        {
            var reader = new FastBufferReader(writer, Allocator.Temp);
            try
            {
                reader.ReadValueSafe(out byte _);
                Deliver(NetworkManager.ServerClientId, type, reader);
            }
            finally { reader.Dispose(); }
        }
    }
}
