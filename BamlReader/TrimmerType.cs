using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BamlReader
{
    internal class TrimmerType
    {
        public string Assembly { get; set; }
        public string TypeFullName { get; set; }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((TrimmerType)obj);
        }

        protected bool Equals(TrimmerType other)
        {
            return Assembly == other.Assembly && TypeFullName == other.TypeFullName;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Assembly, TypeFullName);
        }
    }
}
