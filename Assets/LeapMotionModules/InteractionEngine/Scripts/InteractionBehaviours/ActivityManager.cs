﻿using UnityEngine;
using System;
using System.Collections.Generic;

namespace Leap.Unity.Interaction {

  public class ActivityManager {
    private float _overlapRadius = 0;
    private int _maxDepth = 0;
    private int _layerMask = 0;

    private List<IInteractionBehaviour> _markedBehaviours = new List<IInteractionBehaviour>();
    private Collider[] _colliderResults = new Collider[32];

    //Maps registered objects to their active component, which is always null for inactive but still registered objects
    private Dictionary<IInteractionBehaviour, ActivityMonitor> _registeredBehaviours = new Dictionary<IInteractionBehaviour, ActivityMonitor>();
    //Technically provided by _registerBehaviours, but we want fast iteration over active objects, so pay a little more for a list
    private List<IInteractionBehaviour> _activeBehaviours = new List<IInteractionBehaviour>();

    private List<IInteractionBehaviour> _misbehavingBehaviours = new List<IInteractionBehaviour>();

    public event Action<IInteractionBehaviour> OnActivate;
    public event Action<IInteractionBehaviour> OnDeactivate;

    public float OverlapRadius {
      get {
        return _overlapRadius;
      }
      set {
        _overlapRadius = value;
      }
    }

    public int MaxDepth {
      get {
        return _maxDepth;
      }
      set {
        _maxDepth = value;
      }
    }

    public int LayerMask {
      get {
        return _layerMask;
      }
      set {
        _layerMask = value;
      }
    }

    public IEnumerable<IInteractionBehaviour> RegisteredObjects {
      get {
        return _registeredBehaviours.Keys;
      }
    }

    public ReadonlyList<IInteractionBehaviour> ActiveBehaviours {
      get {
        return _activeBehaviours;
      }
    }

    public void Register(IInteractionBehaviour behaviour) {
      //Do nothing if already registered
      if (_registeredBehaviours.ContainsKey(behaviour)) {
        return;
      }

      behaviour.NotifyRegistered();
      _registeredBehaviours.Add(behaviour, null);
    }

    public void Unregister(IInteractionBehaviour behaviour) {
      //Do nothing if not registered
      if (!_registeredBehaviours.ContainsKey(behaviour)) {
        return;
      }

      if (IsActive(behaviour)) {
        Deactivate(behaviour);
      }

      _registeredBehaviours.Remove(behaviour);

      behaviour.NotifyUnregistered();
    }

    public void NotifyMisbehaving(IInteractionBehaviour behaviour) {
      _misbehavingBehaviours.Add(behaviour);
    }

    public ActivityMonitor Activate(IInteractionBehaviour interactionBehaviour) {
      ActivityMonitor monitor;
      if (_registeredBehaviours.TryGetValue(interactionBehaviour, out monitor)) {
        if (monitor == null) {
          monitor = interactionBehaviour.gameObject.AddComponent<ActivityMonitor>();
          monitor.Init(interactionBehaviour, this);

          //We need to do this in order to force Unity to reconsider collision callbacks for this object
          //Otherwise scripts added in the middle of a collision never recieve the Stay callbacks.
          Collider singleCollider = monitor.GetComponentInChildren<Collider>();
          if (singleCollider != null) {
            Physics.IgnoreCollision(singleCollider, singleCollider, true);
            Physics.IgnoreCollision(singleCollider, singleCollider, false);
          }

          _registeredBehaviours[interactionBehaviour] = monitor;

          monitor.arrayIndex = _activeBehaviours.Count;
          _activeBehaviours.Add(interactionBehaviour);

          if (OnActivate != null) {
            OnActivate(interactionBehaviour);
          }
        }
      }
      return monitor;
    }

    public void Deactivate(IInteractionBehaviour interactionBehaviour) {
      ActivityMonitor monitor;
      if (_registeredBehaviours.TryGetValue(interactionBehaviour, out monitor)) {
        if (monitor != null) {
          //The monitor that is last in the array of monitors
          IInteractionBehaviour lastBehaviour = _activeBehaviours[_activeBehaviours.Count - 1];
          ActivityMonitor lastMonitor = _registeredBehaviours[lastBehaviour];

          //Replace the monitor we are going to destroy with the last monitor
          _activeBehaviours[monitor.arrayIndex] = lastBehaviour;
          //Make sure to update the index of the moved monitor!
          lastMonitor.arrayIndex = monitor.arrayIndex;
          //Remove the empty space at the end
          _activeBehaviours.RemoveAt(_activeBehaviours.Count - 1);

          _registeredBehaviours[interactionBehaviour] = null;
          UnityEngine.Object.Destroy(monitor);

          if (OnDeactivate != null) {
            OnDeactivate(interactionBehaviour);
          }
        }
      }
    }

    public void DeactivateAll() {
      while (_activeBehaviours.Count > 0) {
        Deactivate(_activeBehaviours[0]);
      }
    }

    public bool IsActive(IInteractionBehaviour interactionBehaviour) {
      return _registeredBehaviours[interactionBehaviour] != null;
    }

    public bool IsRegistered(IInteractionBehaviour interactionBehaviour) {
      return _registeredBehaviours.ContainsKey(interactionBehaviour);
    }

    public void Update(Frame frame) {
      markOverlappingObjects(frame.Hands);

      activateAndKeepMarkedObjectsAlive();
    }

    public void UnregisterMisbehavingObjects() {
      for (int i = 0; i < _misbehavingBehaviours.Count; i++) {
        var behaviour = _misbehavingBehaviours[i];
        if (behaviour != null) {
          try {
            Unregister(behaviour);
          } catch (Exception e) {
            Debug.LogException(e);
          }
        }
      }
      _misbehavingBehaviours.Clear();
    }

    private void markOverlappingObjects(List<Hand> hands) {
      _markedBehaviours.Clear();

      switch (hands.Count) {
        case 0:
          break;
        case 1:
          getSphereResults(hands[0], _markedBehaviours);
          break;
#if UNITY_5_4
        case 2:
          //Use capsule collider for efficiency.  Only need one overlap and no duplicates!
          getCapsuleResults(hands[0], hands[1], _markedBehaviours);
          break;
#endif
        default:
          for (int i = 0; i < hands.Count; i++) {
            getSphereResults(hands[i], _markedBehaviours);
          }
          break;
      }
    }

    private void activateAndKeepMarkedObjectsAlive() {
      //This loop doesn't care about duplicates
      for (int i = 0; i < _markedBehaviours.Count; i++) {
        IInteractionBehaviour behaviour = _markedBehaviours[i];
        ActivityMonitor monitor;
        if (_registeredBehaviours.TryGetValue(behaviour, out monitor)) {
          if (monitor == null) {
            monitor = Activate(behaviour);
          }
          monitor.Revive();
        } else {
          Debug.LogError("Should always be registered, since we checked in handleColliderResults");
        }
      }
    }

    private void handleColliderResults(int count, List<IInteractionBehaviour> list) {
      for (int i = 0; i < count; i++) {
        Collider collider = _colliderResults[i];

        //Will happen if someone is using the interaction layers for their own needs
        //We could throw an error/warning?
        if (collider.attachedRigidbody == null) {
          continue;
        }

        IInteractionBehaviour behaviour = collider.attachedRigidbody.GetComponent<IInteractionBehaviour>();

        //Also will happen if someone is abusing layers
        if (behaviour == null) {
          continue;
        }

        //Nothing stopping our overlaps from finding object of other managers, or unregistered objects
        if (!IsRegistered(behaviour)) {
          continue;
        }

        //This will totally add duplicates, we don't care
        list.Add(behaviour);
      }
    }

    private void getSphereResults(Hand hand, List<IInteractionBehaviour> list) {
      int count;
      while (true) {
        count = Physics.OverlapSphereNonAlloc(hand.PalmPosition.ToVector3(),
                                              _overlapRadius,
                                              _colliderResults,
                                              _layerMask,
                                              QueryTriggerInteraction.Ignore);
        if (count < _colliderResults.Length) {
          break;
        }

        //If the count was equal to the array length, we don't know if there are colliders that we missed.
        //Double the length of the array and try again!
        _colliderResults = new Collider[_colliderResults.Length * 2];
      }

      handleColliderResults(count, list);
    }

#if UNITY_5_4
    private void getCapsuleResults(Hand handA, Hand handB, List<IInteractionBehaviour> list) {
      int count;
      while (true) {
        count = Physics.OverlapCapsuleNonAlloc(handA.PalmPosition.ToVector3(),
                                               handB.PalmPosition.ToVector3(),
                                               _overlapRadius,
                                               _colliderResults,
                                               _layerMask,
                                               QueryTriggerInteraction.Ignore);
        if (count < _colliderResults.Length) {
          break;
        }
        _colliderResults = new Collider[_colliderResults.Length * 2];
      }

      handleColliderResults(count, list);
    }
#endif
  }
}