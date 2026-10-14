# Releasing Hotfix

A release is a tag. Pushing `vX.Y.Z` runs `.github/workflows/release.yml`, which publishes the app, packs the
installer with Velopack and creates the GitHub release with every file Velopack wrote. The landing page and the
README link to `releases/latest/download/Hotfix-win-Setup.exe`, so they follow without edits.

1. Bump `<Version>` in `src/Hotfix/Hotfix.csproj`. The workflow refuses a tag that does not match it.
2. On a Windows PC with a GPU, publish and run the yield check. It warms for a minute, takes about two, and fails if
   any job at normal priority slows by more than 10 per cent:

   ```powershell
   dotnet publish src/Hotfix -c Release -r win-x64 --self-contained -o publish
   dotnet run --project tests/YieldCheck -c Release -- publish\Hotfix.exe
   ```

   The yield check needs a quiet PC and exits 3 without a verdict when other apps are busy. Then run the answer
   check, which holds the shipped calculation to what the research post on the landing page says, and prints
   the throughput it quotes:

   ```powershell
   dotnet run --project tests/AnswerCheck -c Release
   ```

   If the numbers moved, update the ones the landing page and the README quote, and the machine they were
   measured on.
3. Commit with a message that says what changed and why, then tag with the release notes as the tag message;
   the workflow uses it as the release body:

   ```powershell
   git tag -a vX.Y.Z -m "Hotfix X.Y.Z" -m "What changed, for people who have it installed."
   git push origin main vX.Y.Z
   ```

4. When the workflow is green, download `Hotfix-win-Setup.exe` from the release on a PC that has never had Hotfix,
   install it, check the Start menu entry, the icon by the clock and the menu, then uninstall it from
   `Settings` > `Apps` > `Installed apps` and check that the `Start with Windows` entry went with it.
