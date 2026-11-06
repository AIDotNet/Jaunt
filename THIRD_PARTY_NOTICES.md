# Third-party notices

Jaunt's own license has not been decided yet (see `design/06-roadmap.md` §1); no `LICENSE` file is created
until that decision is made. This file records the obligations of the third-party components Jaunt uses.
No source code from other JavaScript engines (MoeJS, Jint, Okojo, ClearScript, QuickJS, V8, Jurassic,
YantraJS, goja, Sobek) is copied into this repository; they were studied only as references
(`design/01-research.md`).

## Runtime dependency

### Acornima 1.9.1 — BSD-3-Clause

- Project: <https://github.com/adams85/acornima> (NuGet `Acornima`, license expression `BSD-3-Clause`).
- Used as the JavaScript parser front end and for JS RegExp → .NET `Regex` adaptation
  (`Tokenizer.AdaptRegExp`). Its AST never appears in Jaunt's public API.
- Acornima is a port of the acorn parser; its upstream `NOTICE` file
  (<https://github.com/adams85/acornima/blob/master/NOTICE>) credits acorn (MIT) and esprima-dotnet (BSD-3-Clause).
  Redistributions of Jaunt binaries must carry Acornima's copyright notice, the BSD-3-Clause conditions and
  disclaimer, and the attributions listed in that `NOTICE` file.

Acornima `LICENSE` (<https://github.com/adams85/acornima/blob/master/LICENSE>), reproduced in full:

```text
Copyright (c) Adam Simon. All rights reserved.
BSD 3-Clause License - https://opensource.org/licenses/BSD-3-Clause

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Acornima nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

Acornima `NOTICE` (<https://github.com/adams85/acornima/blob/master/NOTICE>), reproduced in full:

```text
This software is a fork of the following software, originally licensed under the
BSD 3-Clause License:

Esprima.NET - https://github.com/sebastienros/esprima-dotnet

Copyright (c) Sebastien Ros. All rights reserved.
BSD 3-Clause License - https://opensource.org/licenses/BSD-3-Clause

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Esprima-Dotnet nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

A copy of the BSD-3 license can be found here:
https://github.com/sebastienros/esprima-dotnet/blob/main/LICENSE.txt

---

This software also adopts ideas and solutions invented by other free and open
source software projects, including but not limited to:
* acorn (MIT License) - https://github.com/acornjs/acorn
* AcornSharp (MIT License) - https://github.com/MatthewSmit/AcornSharp
* Test262-Harness-dotnet (MIT License) - https://github.com/lahma/test262-harness-dotnet
```

This file is packed into the `Jaunt` NuGet package (at the package root, and as a content file) and is copied as
`Jaunt.THIRD_PARTY_NOTICES.md` into the build and publish output — NativeAOT included — of every application that
references Jaunt, so it accompanies binary redistributions.

### Platform ICU (loaded at run time, not redistributed)

`Intl` (ECMA-402) and Temporal's non-ISO calendars and named time zones call the ICU library that ships with the
operating system (`/usr/lib/libicucore.dylib` on macOS, `icu.dll` on Windows 10+, `libicuuc.so.N`/`libicui18n.so.N`
on Linux) through `NativeLibrary`. Jaunt does not include, link against at build time, or redistribute ICU; its
license (Unicode License V3) applies to the copy provided by the platform.

## Build / test only (not redistributed with the runtime)

| Component | License | Use |
| --- | --- | --- |
| Microsoft.CodeAnalysis.CSharp | MIT | `Jaunt.Generators` source generator (build time) |
| xunit, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | unit tests |
| BenchmarkDotNet | MIT | benchmarks |
| Jint 4.17.0 | BSD-2-Clause | comparison baseline in `bench/` only |
| Okojo.JavaScript.Embedding 0.2.0-preview.2 | MIT | comparison in `bench/Jaunt.Benchmarks/EngineComparison.cs` only |
| Microsoft.ClearScript.V8 7.5.1.1 (+ native V8 packages) | MIT (V8: BSD-3-Clause) | comparison in `bench/Jaunt.Benchmarks/EngineComparison.cs` only |
| MoeJS (`github.com/MoeForge/moejs`) | Apache-2.0 | Go dependency of the cross-engine benchmark runner `bench/cross/moejs-runner` only (fetched by `go mod`, not committed or redistributed) |
| tc39/test262 @ `045bf6f9966ce3291b8fbc1e0403cd97b9201b00` | BSD-3-Clause | conformance tests, fetched into `artifacts/` (not committed) |

## Unicode Character Database and CLDR (data files)

`src/Jaunt/Runtime/Regex/UnicodeData.g.cs` is generated by `tools/unicode/generate.py` from the Unicode Character
Database 17.0.0 (`Scripts.txt`, `ScriptExtensions.txt`, `PropList.txt`, `DerivedCoreProperties.txt`,
`DerivedNormalizationProps.txt`, `extracted/DerivedGeneralCategory.txt`, `extracted/DerivedBinaryProperties.txt`,
`emoji/emoji-data.txt`, `emoji-sequences.txt`, `emoji-zwj-sequences.txt`, `PropertyValueAliases.txt`,
`PropertyAliases.txt`, `CaseFolding.txt`), https://www.unicode.org/Public/17.0.0/.
`src/Jaunt/Runtime/CaseMapping.g.cs` is generated by `tools/unicode/casing.py` from `UnicodeData.txt` and
`SpecialCasing.txt` of the same version.
`src/Jaunt/Builtins/Intl/IntlLocaleData.g.cs` is generated by `tools/unicode/cldr_locale.py` from the Unicode Common
Locale Data Repository (CLDR) 48 (`common/supplemental/supplementalMetadata.xml` alias data and `common/bcp47/*.xml`),
https://github.com/unicode-org/cldr/tree/release-48. CLDR data is distributed under the same Unicode License V3
reproduced below.

```text
UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2025 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```
