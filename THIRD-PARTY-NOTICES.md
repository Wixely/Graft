# Third-party notices

Graft redistributes the components below. Some are compiled into the published executable and some are
vendored as files under `src/Graft.Client/wwwroot/vendor/`, so this notice is embedded in the server and
served at `/-/licences`: a single-file NativeAOT binary is easily separated from a file in a repository, and
the attribution has to travel with the binary to be worth anything.

Each component remains under its own licence. Nothing here is modified unless stated.

## Compiled into the server and client

- **Markdig** — BSD 2-Clause licence, © 2016-2026 Alexandre Mutel.
  <https://github.com/xoofx/markdig>. Used to render repository README files. See the BSD 2-Clause terms
  below, which this redistribution reproduces as that licence requires.
- **Dapper** and **Dapper.AOT** — Apache Licence 2.0, © Marc Gravell and contributors.
  <https://github.com/DapperLib/Dapper> and <https://github.com/DapperLib/DapperAOT>.
  A copy of the licence is at <https://www.apache.org/licenses/LICENSE-2.0>. Neither is modified.
- **Microsoft.AspNetCore.Components.WebAssembly**, **Microsoft.Data.Sqlite**,
  **Microsoft.Extensions.DependencyInjection**, **Microsoft.Extensions.Logging**,
  **Microsoft.Extensions.Hosting.Systemd** and **Microsoft.Extensions.Hosting.WindowsServices** —
  MIT licence, © Microsoft Corporation. <https://github.com/dotnet/runtime> and
  <https://github.com/dotnet/aspnetcore>.
- **DnaX.Data.Migrations**, **DnaX.Data.Migrations.Sqlite** and **DnaX.Uploads** —
  MIT licence, © 2026 DNAX contributors. <https://github.com/Wixely/DnaX>.

## Compiled into the Model Context Protocol server

`Graft.Mcp` is a separate executable, so these are redistributed with it and not with the forge. It prints
the same notice, for the same reason the server does.

- **ModelContextProtocol** and **ModelContextProtocol.Core** 2.2.0 — Apache Licence 2.0,
  © Model Context Protocol, a Series of LF Projects, LLC.
  <https://csharp.sdk.modelcontextprotocol.io/>. A copy of the licence is at
  <https://www.apache.org/licenses/LICENSE-2.0>. Neither is modified.
- **Microsoft.Extensions.AI.Abstractions**, **Microsoft.Extensions.Hosting** and the
  **Microsoft.Extensions.*** configuration, logging, options and dependency injection packages they bring
  with them — MIT licence, © Microsoft Corporation. <https://github.com/dotnet/runtime> and
  <https://github.com/dotnet/extensions>.

## Vendored browser assets

- **mermaid** 11.15.0 — MIT licence, © 2014-2022 Knut Sveidqvist.
  <https://github.com/mermaid-js/mermaid>. Unmodified; see
  `src/Graft.Client/wwwroot/vendor/mermaid/` for the upstream licence text and a provenance record.
- **DnaX.Uploads browser module** (`uploads.js`) — MIT licence, © 2026 DNAX contributors.
  <https://github.com/Wixely/DnaX>. Unmodified; see
  `src/Graft.Client/wwwroot/vendor/dnax-uploads/` for the upstream licence text and a provenance record.

## Statically linked into the Linux build

The `GraftServerStatic` configuration links these into the executable, which makes that executable a binary
redistribution of them. They are not present in the ordinary managed build.

- **SQLite** — public domain, no conditions. <https://www.sqlite.org/copyright.html>
- **OpenSSL** — Apache Licence 2.0, © The OpenSSL Project Authors.
  <https://www.openssl.org/source/license.html>
- **ICU** — Unicode Licence, © Unicode, Inc. and others. <https://github.com/unicode-org/icu/blob/main/LICENSE>

## Development and test only

Not redistributed in any published artifact, listed for completeness.

- **Git** 2.53 and **glab** 1.58.0 are invoked as external clients during acceptance runs and are neither
  bundled nor modified.

## MIT licence

The MIT-licensed components above are redistributed under these terms, with copyright held as attributed:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction, including
> without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
> following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
> LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
> EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
> IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
> USE OR OTHER DEALINGS IN THE SOFTWARE.

## BSD 2-Clause licence

Markdig is redistributed under these terms, © 2016-2026 Alexandre Mutel, all rights reserved:

> Redistribution and use in source and binary forms, with or without modification, are permitted provided
> that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
>    following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and
>    the following disclaimer in the documentation and/or other materials provided with the distribution.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
> WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
> PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY
> DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
> PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
> HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
> NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
> POSSIBILITY OF SUCH DAMAGE.

## Keeping this accurate

Update this file in the same change that adds, removes or upgrades a dependency. A dependency whose licence
obligations are not recorded here should not ship.
