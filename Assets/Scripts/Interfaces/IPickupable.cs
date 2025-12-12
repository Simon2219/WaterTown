using UnityEngine;

namespace Interfaces
{
    /// <summary>
    /// Interface for objects that can be picked up, moved, and placed in the world.
    /// Supports both building new objects and moving existing ones.
    /// </summary>
    public interface IPickupable
    {
        /// <summary>
        /// True if this object is currently being picked up/moved by the player.
        /// </summary>
        bool IsPickedUp { get; set; }
        
        /// <summary>
        /// True if this object can currently be placed at its current position.
        /// The object is responsible for validating its own placement.
        /// </summary>
        bool CanBePlaced { get; }
        
        /// <summary>
        /// The transform of the pickupable object.
        /// </summary>
        Transform Transform { get; }
        
        /// <summary>
        /// The GameObject of the pickupable object.
        /// </summary>
        GameObject GameObject { get; }
        
        /// <summary>
        /// Called when the object is picked up (either newly spawned or existing).
        /// Object should handle its own visual changes (materials, disable colliders, etc.).
        /// </summary>
        /// <param name="isNewObject">True if this is a newly spawned object, false if moving existing</param>
        void OnPickedUp(bool isNewObject);
        
        /// <summary>
        /// Called when the object is placed successfully.
        /// Object should restore its normal state and register with relevant managers.
        /// </summary>
        void OnPlaced();
        
        /// <summary>
        /// Called when placement is cancelled.
        /// For new objects: they should be destroyed.
        /// For existing objects: they should return to their original position/state.
        /// </summary>
        void OnPlacementCancelled();
        
        /// <summary>
        /// Update the object's validity state for visual feedback.
        /// Called each frame while picked up to show if placement is valid.
        /// </summary>
        void UpdateValidityVisuals();
    }
}

