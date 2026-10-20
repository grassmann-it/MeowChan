class Meowchan < Formula
  desc "Local imageboard archive, reader, downloads and media gallery"
  homepage "https://github.com/tfgrass/MeowChan"
  url "https://github.com/tfgrass/MeowChan/archive/a98759e819f233bee0de806cb4f0b9fbd535d2cd.tar.gz"
  version "0.9.0-preview.3"
  sha256 "711cfdc9fa5b748431940b468c3935522156345fc82831bcae99d3f529bd7208"
  license "Artistic-2.0"

  depends_on "python@3.14" => :build
  depends_on macos: :sequoia

  # global.json requires this exact SDK. Homebrew's rolling dotnet is not a substitute.
  on_arm do
    resource "dotnet-sdk" do
      url "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.111/dotnet-sdk-10.0.111-osx-arm64.tar.gz"
      sha256 "2f96158f7309f659e62281e826b3fe0dd4a2763a79de545851a8649f06ba3b67"
    end
  end

  on_intel do
    resource "dotnet-sdk" do
      url "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.111/dotnet-sdk-10.0.111-osx-x64.tar.gz"
      sha256 "f018c77ebb89cca05bc8bcd77335d74abce5aecf726fd5e0dafbdae144cbc93c"
    end
  end

  # Current Homebrew runs this phase with network access, then install without it.
  def fetch
    prepare_sdk
    system dotnet, "restore", "MeowChan.csproj",
           "--artifacts-path", build_artifacts,
           "-p:Configuration=Release", "-p:RuntimeIdentifier=#{runtime_id}",
           "-p:SelfContained=true", "-p:PublishSingleFile=true",
           "--locked-mode", "-warnaserror:NU1900"
  end

  def install
    prepare_sdk
    system dotnet, "publish", "MeowChan.csproj", "-c", "Release", "-r", runtime_id,
           "--artifacts-path", build_artifacts,
           "--self-contained", "true", "-p:PublishSingleFile=true", "--no-restore",
           "--output", buildpath/"publish"
    python = Formula["python@3.14"].opt_bin/"python3.14"
    system python, "scripts/check-video-native.py", buildpath/"publish", runtime_id
    system python, "scripts/package-macos.py", "--publish-dir", buildpath/"publish",
           "--output-dir", buildpath/"bundle", "--version", "0.9.0"
    system python, "scripts/check-video-native.py",
           buildpath/"bundle/MeowChan.app/Contents/MacOS", runtime_id
    # The SDK ad-hoc signs the generated apphost. No Developer ID or notarization.
    system "/usr/bin/codesign", "--verify", "--strict",
           buildpath/"bundle/MeowChan.app/Contents/MacOS/MeowChan"
    libexec.install buildpath/"bundle/MeowChan.app"
    bin.write_exec_script libexec/"MeowChan.app/Contents/MacOS/MeowChan"
    bin.install_symlink bin/"MeowChan" => "meowchan"
  end

  def caveats
    <<~EOS
      This formula builds the pinned preview locally. Its .NET runtime and selected
      VLC plugin tree are bundled; there is no runtime dependency on the build SDK.
      It is not a Developer ID signed or notarized application. macOS security checks
      still apply. Native macOS installation and playback acceptance remain open.

      Launch from a terminal with:
        meowchan

      To expose the installed bundle in your own Applications folder:
        mkdir -p ~/Applications
        ln -sfn "#{opt_libexec}/MeowChan.app" ~/Applications/MeowChan.app

      Remove that user-created symlink yourself after uninstalling the formula.
    EOS
  end

  test do
    app = libexec/"MeowChan.app/Contents/MacOS"
    assert_path_exists app/"MeowChan"
    assert_path_exists bin/"meowchan"
    assert_path_exists libexec/"MeowChan.app/Contents/Info.plist"
    native = app/"libvlc"/runtime_id/"lib"
    expected_cpu = Hardware::CPU.arm? ? 0x100000c : 0x1000007
    %w[libvlc.dylib libvlccore.dylib vlc/plugins/libmkv_plugin.dylib
       vlc/plugins/libvpx_plugin.dylib vlc/plugins/libvorbis_plugin.dylib
       vlc/plugins/libopus_plugin.dylib].each do |name|
      library = native/name
      assert_path_exists library
      assert_equal [0xfeedfacf, expected_cpu], File.binread(library, 8).unpack("V2")
    end
    system "/usr/bin/codesign", "--verify", "--strict", app/"MeowChan"
  end

  private

  def runtime_id
    Hardware::CPU.arm? ? "osx-arm64" : "osx-x64"
  end

  def dotnet
    buildpath/"artifacts/homebrew-tools/dotnet-sdk/dotnet"
  end

  def build_artifacts
    buildpath/"artifacts/homebrew"
  end

  def prepare_sdk
    # MeowChan excludes artifacts/** from implicit SDK compile/content globs.
    sdk = buildpath/"artifacts/homebrew-tools/dotnet-sdk"
    resource("dotnet-sdk").stage sdk unless dotnet.exist?
    ENV.prepend_path "PATH", sdk
    ENV["DOTNET_ROOT"] = sdk.to_s
    ENV["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    ENV["DOTNET_CLI_HOME"] = (buildpath/"artifacts/homebrew-state/dotnet-home").to_s
    ENV["NUGET_PACKAGES"] = (buildpath/"artifacts/homebrew-state/nuget-packages").to_s
    ENV["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    ENV["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    ENV["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true"
    ENV["DOTNET_NOLOGO"] = "1"
  end
end
