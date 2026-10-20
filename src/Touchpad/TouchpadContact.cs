namespace ThreeFingerDragLite.Touchpad;

// Ported from emoacht/RawInput.Touchpad via ThreeFingerDragOnWindows (MIT).
public struct TouchpadContact : IEquatable<TouchpadContact> {
    public int ContactId { get; }
    public int X { get; }
    public int Y { get; }

    public TouchpadContact(int contactId, int x, int y) {
        ContactId = contactId;
        X = x;
        Y = y;
    }

    public bool Equals(TouchpadContact other) => ContactId == other.ContactId && X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is TouchpadContact other && Equals(other);
    public override int GetHashCode() => (ContactId, X, Y).GetHashCode();
    public override string ToString() => $"ID:{ContactId} {X},{Y}";

    public float GetDist2D(TouchpadContact other) {
        float dx = other.X - X, dy = other.Y - Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

internal class TouchpadContactCreator {
    public int? ContactId { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }

    public bool TryCreate(out TouchpadContact contact) {
        if (ContactId.HasValue && X.HasValue && Y.HasValue) {
            contact = new TouchpadContact(ContactId.Value, X.Value, Y.Value);
            return true;
        }
        contact = default;
        return false;
    }

    public void Clear() {
        ContactId = null;
        X = null;
        Y = null;
    }
}
