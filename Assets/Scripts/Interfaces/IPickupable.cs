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
        bool IsPickedUp { get; }
        
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
        /// Initiates pickup of this object.
        /// Object handles its own state changes and fires appropriate events.
        /// </summary>
        /// <param name="isNewObject">True if this is a newly spawned object, false if moving existing</param>
        void PickUp(bool isNewObject);
        
        /// <summary>
        /// Confirms placement of this object at current position.
        /// Object handles its own state changes and fires appropriate events.
        /// </summary>
        void Place();
        
        /// <summary>
        /// Cancels placement of this object.
        /// For new objects: they should be destroyed.
        /// For existing objects: they should return to their original position/state.
        /// </summary>
        void CancelPlacement();
        
        /// <summary>
        /// Update the object's visual feedback for placement validity.
        /// Called each frame while picked up to show if placement is valid.
        /// </summary>
        void UpdateValidityVisuals();
    }
}
