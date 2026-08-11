// Named by nearly every file here. `CultureInfo` and `NumberStyles` in particular: CA1305 is an
// error in this tree (.editorconfig § "Correctness worth breaking a build for"), so every parse and
// format in the SDK spells its IFormatProvider, and `using System.Globalization;` in forty files is
// noise rather than information.
global using System.Globalization;
global using System.Net;
global using System.Net.Http;
global using System.Text;
