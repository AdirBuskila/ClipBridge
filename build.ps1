# Builds dist\ClipBridge.apk and dist\ClipBridge.exe.
# Requires the Android SDK (platforms;android-34, build-tools;34.0.0), a JDK and the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "$env:LOCALAPPDATA\Android\Sdk" }
$bt = "$sdk\build-tools\34.0.0"
$jar = "$sdk\platforms\android-34\android.jar"
$src = "$root\android"
$b = "$root\build\android"
$dist = "$root\dist"

# The signing key lives outside the project so it can never be committed. Android only
# installs updates signed with the same key, so back this folder up.
$signing = if ($env:CLIPBRIDGE_SIGNING_DIR) { $env:CLIPBRIDGE_SIGNING_DIR } else { "$env:USERPROFILE\.clipbridge-signing" }
$ks = "$signing\clipbridge.keystore"
$pwFile = "$signing\password.txt"

function Run($exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "failed: $exe $args" }
}

foreach ($dir in $b, $dist) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
New-Item -ItemType Directory -Force "$b\gen", "$b\classes", "$b\dex", $dist, $signing | Out-Null

if (-not (Test-Path $ks)) {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [Convert]::ToBase64String($bytes) | Set-Content $pwFile -Encoding ascii -NoNewline
    $newPw = Get-Content $pwFile -Raw
    Run keytool -genkeypair -keystore $ks -storepass $newPw -keypass $newPw -alias clipbridge `
        -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=ClipBridge"
}
$pw = Get-Content $pwFile -Raw

Run "$bt\aapt2.exe" compile --dir "$src\res" -o "$b\res.zip"
Run "$bt\aapt2.exe" link -I $jar --manifest "$src\AndroidManifest.xml" --java "$b\gen" `
    --min-sdk-version 26 --target-sdk-version 34 -o "$b\base.apk" "$b\res.zip"

$sources = @(Get-ChildItem "$src\src", "$b\gen" -Recurse -Filter *.java | ForEach-Object FullName)
Run javac -nowarn --release 8 -cp $jar -d "$b\classes" @sources

$classes = @(Get-ChildItem "$b\classes" -Recurse -Filter *.class | ForEach-Object FullName)
Run "$bt\d8.bat" --release --min-api 26 --lib $jar --output "$b\dex" @classes

# aapt keeps resources.arsc uncompressed, which Android 11+ requires.
Copy-Item "$b\dex\classes.dex" "$b\classes.dex"
Push-Location $b
try { Run "$bt\aapt.exe" add base.apk classes.dex | Out-Null } finally { Pop-Location }

Run "$bt\zipalign.exe" -f -p 4 "$b\base.apk" "$b\aligned.apk"
Run "$bt\apksigner.bat" sign --ks $ks --ks-pass "pass:$pw" --key-pass "pass:$pw" `
    --v4-signing-enabled false --out "$dist\ClipBridge.apk" "$b\aligned.apk"
Run "$bt\apksigner.bat" verify "$dist\ClipBridge.apk"

# Self-contained, so friends don't need to install .NET.
Run dotnet publish "$root\pc\ClipBridge.csproj" -c Release -r win-x64 --self-contained true `
    "-p:PublishSingleFile=true" "-p:IncludeNativeLibrariesForSelfExtract=true" `
    "-p:EnableCompressionInSingleFile=true" -o "$dist" --nologo -v quiet

Get-ChildItem $dist | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
