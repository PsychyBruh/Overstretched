using UnityEngine;

namespace Overstretched
{
    /// <summary>
    /// Moves a preview instance between its floor spot and the ceiling directly above it, so the
    /// ceiling clips (authored on the floor) can be watched upside down where they belong.
    /// </summary>
    public class PreviewPlacement : MonoBehaviour
    {
        private Vector3 floorPosition;
        private Quaternion floorRotation;
        private float ceilingY;

        public void Init(Vector3 position, Quaternion rotation)
        {
            floorPosition = position;
            floorRotation = rotation;
            var mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            ceilingY = Physics.Raycast(position + Vector3.up * 0.5f, Vector3.up, out var hit, 15f, mask, QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : position.y + 4.8f;
        }

        public void SetCeiling(bool onCeiling)
        {
            if (onCeiling)
            {
                // Flip about the creature's forward axis (+Z): its "floor" contact points land on the ceiling.
                transform.SetPositionAndRotation(new Vector3(floorPosition.x, ceilingY, floorPosition.z),
                    floorRotation * Quaternion.AngleAxis(180f, Vector3.forward));
            }
            else
            {
                transform.SetPositionAndRotation(floorPosition, floorRotation);
            }
        }
    }
}
