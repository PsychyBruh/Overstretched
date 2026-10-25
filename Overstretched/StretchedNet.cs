using Unity.Collections;
using Unity.Netcode;

namespace Overstretched
{
    /// <summary>
    /// The Stretched's custom events, sent as NGO named messages (mod DLLs can't declare their own RPCs
    /// without IL post-processing). Every message starts with the enemy's NetworkObjectId and a type byte.
    /// </summary>
    public static class StretchedNet
    {
        public const string Channel = "Overstretched.Stretched";

        public enum Msg : byte
        {
            Flicker = 1,     // host -> all: Vector3 position, float radius
            Mark,            // host -> all: int marked player index, int escapes
            Mode,            // host -> all: byte visual mode
            Clip,            // host -> all: string clip, float crossfade, float lock, int snapFrame, float speed
            Knock,           // host -> all: Vector3 position, int count
            Voice,           // host -> all: int source player index
            Kill,            // host -> all: int victim index
            Breath,          // host -> all: int player index
            Suit,            // host -> all: int player index whose suit it now wears
            Teleport,        // host -> all: Vector3 position, float yaw, bool outside (snap, no smoothing)
            Stare = 32,      // client -> host: int player index
            Lit,             // client -> host: int player index
            Touch,           // client -> host: int player index
            Noise,           // client -> host: Vector3 position, float loudness
        }

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
            reader.ReadValueSafe(out ulong objectId);
            reader.ReadValueSafe(out byte type);
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(objectId, out var obj)) return;
            var ai = obj.GetComponent<StretchedAI>();
            if (ai != null) ai.Receive(sender, (Msg)type, reader);
        }

        /// <summary>Host only: deliver to every client, including the host itself.</summary>
        public static void ToAll(StretchedAI ai, Msg type, Body body = null)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            var writer = Write(ai, type, body);
            try
            {
                foreach (var id in nm.ConnectedClientsIds)
                    if (id != NetworkManager.ServerClientId)
                        nm.CustomMessagingManager.SendNamedMessage(Channel, id, writer, NetworkDelivery.ReliableSequenced);
                DeliverLocally(ai, type, writer);
            }
            finally { writer.Dispose(); }
        }

        /// <summary>Any client: tell the host something this client noticed about its own player.</summary>
        public static void ToHost(StretchedAI ai, Msg type, Body body = null)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            var writer = Write(ai, type, body);
            try
            {
                if (nm.IsServer) DeliverLocally(ai, type, writer);
                else nm.CustomMessagingManager.SendNamedMessage(Channel, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
            finally { writer.Dispose(); }
        }

        private static FastBufferWriter Write(StretchedAI ai, Msg type, Body body)
        {
            var writer = new FastBufferWriter(64, Allocator.Temp, 2048);
            writer.WriteValueSafe(ai.NetworkObjectId);
            writer.WriteValueSafe((byte)type);
            body?.Invoke(ref writer);
            return writer;
        }

        private static void DeliverLocally(StretchedAI ai, Msg type, FastBufferWriter writer)
        {
            var reader = new FastBufferReader(writer, Allocator.Temp);
            try
            {
                reader.ReadValueSafe(out ulong _);
                reader.ReadValueSafe(out byte _);
                ai.Receive(NetworkManager.ServerClientId, type, reader);
            }
            finally { reader.Dispose(); }
        }
    }
}
