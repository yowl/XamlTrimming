namespace BamlReader
{
    internal class TrimmerProperty
    {
        public string Assembly { get; set; }
        public string TypeFullName { get; set; }
        public string Name { get; set; }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((TrimmerProperty)obj);
        }

        protected bool Equals(TrimmerProperty other)
        {
            return Assembly == other.Assembly && TypeFullName == other.TypeFullName && Name == other.Name;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Assembly, TypeFullName, Name);
        }
    }
}
