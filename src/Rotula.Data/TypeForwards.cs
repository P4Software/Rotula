using System.Runtime.CompilerServices;
using Rotula.Data;

// DatabaseQueryReference moved to Rotula.Data.Abstractions (so file-format code needs no database driver). Anything compiled against
// the older Rotula.Data.dll alone keeps working through this forward.
[assembly: TypeForwardedTo(typeof(DatabaseQueryReference))]
