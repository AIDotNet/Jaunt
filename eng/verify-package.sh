#!/usr/bin/env bash
# Package gate: checks what a .nupkg contains, then installs it into a fresh project outside this repository and runs
# JavaScript plus source-generated bindings, the same way a NuGet consumer would.
# Usage: eng/verify-package.sh <directory holding Jaunt.*.nupkg>
# Env:   TFMS  target frameworks to run (default: "net8.0 net10.0 net11.0"; each needs its runtime installed)
set -euo pipefail
PKG_DIR="$(cd "${1:?usage: verify-package.sh <package-directory>}" && pwd)"
TFMS="${TFMS:-net8.0 net10.0 net11.0}"

shopt -s nullglob
packages=("$PKG_DIR"/Jaunt.*.nupkg)
if [ "${#packages[@]}" -ne 1 ]; then echo "FAIL: expected one Jaunt.*.nupkg in $PKG_DIR, found ${#packages[@]}"; exit 1; fi
nupkg="${packages[0]}"
version="$(basename "$nupkg" .nupkg)"; version="${version#Jaunt.}"
echo "=== $(basename "$nupkg")"

entries="$(unzip -Z1 "$nupkg")"
for expected in lib/net8.0/Jaunt.dll lib/net10.0/Jaunt.dll lib/net11.0/Jaunt.dll analyzers/dotnet/cs/Jaunt.Generators.dll \
                README.md THIRD_PARTY_NOTICES.md contentFiles/any/any/Jaunt.THIRD_PARTY_NOTICES.md; do
  if ! grep -qxF "$expected" <<<"$entries"; then echo "FAIL: $expected is missing from the package"; exit 1; fi
done
if [ ! -f "${nupkg%.nupkg}.snupkg" ]; then echo "FAIL: no symbol package next to the .nupkg"; exit 1; fi
nuspec="$(unzip -p "$nupkg" Jaunt.nuspec)"
if grep -q 'Jaunt.Generators' <<<"$nuspec"; then echo "FAIL: Jaunt.Generators leaked into the package dependencies"; exit 1; fi
if ! grep -qF '<license type="expression">MIT</license>' <<<"$nuspec"; then echo "FAIL: the package does not declare the MIT license"; exit 1; fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
export NUGET_PACKAGES="$WORK/packages" # a private cache: never resolve an older extraction of this same version

cat > nuget.config <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$PKG_DIR" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
cat > Consumer.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>${TFMS// /;}</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jaunt" Version="$version" />
  </ItemGroup>
  <ItemGroup Condition="'\$(OS)' == 'Windows_NT'">
    <PackageReference Include="Microsoft.ICU.ICU4C.Runtime" Version="72.1.0.3" />
  </ItemGroup>
</Project>
EOF
cat > Program.cs <<'EOF'
using Jaunt;

using var engine = new JsEngine();
var sum = engine.Evaluate("[1, 2, 3].map(x => x * 2).reduce((a, b) => a + b, 0)").ToString();
if (sum != "12") throw new Exception("Evaluate returned " + sum);

// fails to compile unless the generator bundled in the package ran
var module = JsCompiler.CompileModule("export function double(input) { return { value: input.value * 2 } }",
    new JsCompileOptions { SourceName = "double.mjs" });
await using var plugin = JsPlugin.Create(module, "double", Bindings.Default.Payload, Bindings.Default.Payload, new JsPluginOptions());
var result = await plugin.InvokeAsync(new Payload(21));
if (result.Value != 42) throw new Exception("Plugin returned " + result);

Console.WriteLine("package OK");

public sealed record Payload(int Value);

[JsSerializable(typeof(Payload))]
public partial class Bindings : JsBindingContext { }
EOF

for tfm in $TFMS; do
  echo "=== consumer on $tfm"
  dotnet run -c Release -f "$tfm"
  if [ ! -f "bin/Release/$tfm/Jaunt.THIRD_PARTY_NOTICES.md" ]; then echo "FAIL: notices not copied to the $tfm output"; exit 1; fi
done
echo "OK: package verified"
