# Third-party notices

Deming itself is licensed under Apache-2.0 (see [LICENSE](LICENSE) and [NOTICE](NOTICE)).
The `deming` binary and the `Deming.<rid>` tool packages are self-contained: they include the
components below, each under its own license.

| Component | Used for | License |
|---|---|---|
| .NET runtime and ASP.NET Core (Microsoft, .NET Foundation and contributors) | Runtime, web server, Identity, Razor components | MIT |
| Microsoft.Data.Sqlite (Microsoft, .NET Foundation and contributors) | SQLite data provider | MIT |
| SQLitePCLRaw (SourceGear, Eric Sink) | Native SQLite bindings and bundle | Apache-2.0 |
| SQLite | Database engine (`e_sqlite3`) | Public domain |
| Markdig (Alexandre Mutel) | Markdown rendering of finding bodies in the panel | BSD-2-Clause |

Build-time only, not part of the binary: AspNetCore.SassCompiler and Dart Sass (MIT) compile the
panel stylesheet; xunit.v3 (Apache-2.0) runs the tests.

## MIT License

Applies to the .NET runtime, ASP.NET Core and Microsoft.Data.Sqlite.

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Apache License 2.0

Applies to SQLitePCLRaw. The license text is the same as Deming's own: see [LICENSE](LICENSE).

## SQLite

SQLite is in the public domain: https://www.sqlite.org/copyright.html

## BSD 2-Clause License

Applies to Markdig.

Copyright (c) Alexandre Mutel. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
