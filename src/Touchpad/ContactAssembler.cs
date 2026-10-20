using ThreeFingerDragLite.Native;

namespace ThreeFingerDragLite.Touchpad;

/// <summary>
/// Assembles complete contact lists from raw input batches. Precision touchpads may split
/// a contact report across several WM_INPUT messages; the declared contact count tells us
/// when the list is complete. Ported from ThreeFingerDragOnWindows ContactsManager (MIT),
/// including the rare "new incomplete batch while an old partial list is pending" path:
/// the old list is flushed AND the new batch is kept for completion (the original submits
/// both; this port returns 0..2 complete frames per call instead of using a callback).
/// </summary>
public class ContactAssembler {
    private readonly List<TouchpadContact> _lastContacts = new();
    private uint _targetContactCount;

    /// <summary>Returns the complete contact frames contained in this batch (0, 1 or 2 lists).</summary>
    public List<TouchpadContact[]> Receive(List<TouchpadContact>? contacts, uint declaredCount) {
        var frames = new List<TouchpadContact[]>(2);
        if (contacts == null || contacts.Count == 0) return frames;

        // Regular contact list
        if (declaredCount == contacts.Count) {
            _lastContacts.Clear();
            frames.Add(contacts.ToArray());
            return frames;
        }

        // Partial contact list (always sent after an incomplete contact list)
        if (declaredCount == 0) {
            _lastContacts.AddRange(contacts);
            Deduplicate(_lastContacts);

            if (_targetContactCount == 0) return frames;

            if (_lastContacts.Count > _targetContactCount) {
                frames.Add(_lastContacts.Take((int)_targetContactCount).ToArray());
                _lastContacts.Clear();
            } else if (_lastContacts.Count == _targetContactCount) {
                frames.Add(_lastContacts.ToArray());
                _lastContacts.Clear();
            }
            return frames;
        }

        // An old partial list is still pending: flush it (padding with the last contact
        // when it never completed), then fall through so the new batch is not lost.
        if (_lastContacts.Count != 0) {
            if (_lastContacts.Count < _targetContactCount) {
                var lastContact = _lastContacts[^1];
                int maxId = _lastContacts.Max(c => c.ContactId);
                for (int i = 1; i <= _targetContactCount - _lastContacts.Count; i++)
                    _lastContacts.Add(new TouchpadContact(maxId + i, lastContact.X, lastContact.Y));
            } else if (_lastContacts.Count > _targetContactCount) {
                _lastContacts.RemoveRange((int)_targetContactCount, _lastContacts.Count - (int)_targetContactCount);
            }

            frames.Add(_lastContacts.ToArray());
            _lastContacts.Clear();
        }

        // Regular contact list with more contacts than expected (unlikely to happen)
        if (declaredCount <= contacts.Count) {
            frames.Add(contacts.Take((int)declaredCount).ToArray());
            return frames;
        }

        // Incomplete list: keep it and wait for the follow-up partial batches.
        _targetContactCount = declaredCount;
        _lastContacts.Clear();
        _lastContacts.AddRange(contacts);
        return frames;
    }

    private static void Deduplicate(List<TouchpadContact> contacts) {
        var seen = new HashSet<int>();
        int write = 0;
        foreach (var contact in contacts) {
            if (seen.Add(contact.ContactId)) contacts[write++] = contact;
        }
        contacts.RemoveRange(write, contacts.Count - write);
    }
}
