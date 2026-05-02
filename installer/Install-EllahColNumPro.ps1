<#
.SYNOPSIS
    ELLAH-ColNum Pro — Professional Installer for Revit 2025
    Shows a WPF installation window. Run as Administrator for best results.
#>

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$ScriptDir   = $PSScriptRoot
$ProjectRoot = Split-Path $ScriptDir -Parent
$RevitVersion = "2025"
$AddinsFolder = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
$DllSource    = "$ProjectRoot\src\Revit\bin\Debug\EllahColNumPro.dll"
$AddinSource  = "$ProjectRoot\src\Revit\EllahColNumPro.addin"

# ── Build first ───────────────────────────────────────────────────────────────
$buildOutput = & dotnet build "$ProjectRoot\src\Revit\RevitColumnNumberer.Revit.csproj" `
    --configuration Debug 2>&1 | Out-String
$buildOk = $LASTEXITCODE -eq 0

# ── WPF Window XAML ──────────────────────────────────────────────────────────
[xml]$xaml = @"
<Window
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="ELLAH-ColNum Pro Installer"
    Width="520" Height="420"
    ResizeMode="NoResize"
    WindowStartupLocation="CenterScreen"
    Background="#1E1E2E">

  <Window.Resources>
    <Style TargetType="TextBlock">
      <Setter Property="Foreground" Value="#CDD6F4"/>
      <Setter Property="FontFamily" Value="Segoe UI"/>
    </Style>
    <Style TargetType="Button">
      <Setter Property="Background" Value="#89B4FA"/>
      <Setter Property="Foreground" Value="#1E1E2E"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="FontSize"   Value="13"/>
      <Setter Property="BorderThickness" Value="0"/>
      <Setter Property="Padding" Value="24,8"/>
      <Setter Property="Cursor" Value="Hand"/>
    </Style>
  </Window.Resources>

  <Grid Margin="36,28,36,28">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <!-- Logo / Title -->
    <StackPanel Grid.Row="0" Margin="0,0,0,20">
      <TextBlock Text="ELLAH-ColNum Pro" FontSize="26" FontWeight="Bold"
                 Foreground="#89B4FA"/>
      <TextBlock Text="Smart Column Numbering for Revit 2025" FontSize="12"
                 Foreground="#6C7086" Margin="0,4,0,0"/>
    </StackPanel>

    <!-- Divider -->
    <Rectangle Grid.Row="1" Height="1" Fill="#313244" Margin="0,0,0,20"/>

    <!-- Info -->
    <StackPanel Grid.Row="2" Margin="0,0,0,16">
      <TextBlock FontSize="12" TextWrapping="Wrap" Foreground="#BAC2DE">
        This installer will deploy ELLAH-ColNum Pro to your Revit 2025 Add-Ins folder.
        Once installed, open Revit and find the plugin under the <Bold>Add-Ins</Bold> tab.
      </TextBlock>
    </StackPanel>

    <!-- Status log -->
    <Border Grid.Row="3" Background="#181825" CornerRadius="6" Padding="12" Margin="0,0,0,20">
      <ScrollViewer VerticalScrollBarVisibility="Auto">
        <TextBlock x:Name="LogText" FontSize="11" FontFamily="Consolas"
                   Foreground="#A6E3A1" TextWrapping="Wrap"/>
      </ScrollViewer>
    </Border>

    <!-- Progress bar -->
    <ProgressBar x:Name="ProgressBar" Grid.Row="4" Height="6"
                 Background="#313244" Foreground="#89B4FA"
                 BorderThickness="0" Margin="0,0,0,20"
                 Minimum="0" Maximum="100" Value="0"/>

    <!-- Buttons -->
    <Grid Grid.Row="5">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="12"/>
        <ColumnDefinition Width="Auto"/>
      </Grid.ColumnDefinitions>
      <TextBlock x:Name="StatusText" Grid.Column="0" FontSize="11"
                 Foreground="#6C7086" VerticalAlignment="Center"/>
      <Button x:Name="InstallBtn" Grid.Column="1" Content="Install"/>
      <Button x:Name="CloseBtn"   Grid.Column="3" Content="Close"
              Background="#313244" Foreground="#CDD6F4"/>
    </Grid>
  </Grid>
</Window>
"@

$reader = [System.Xml.XmlNodeReader]::new($xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)

$logText     = $window.FindName("LogText")
$progressBar = $window.FindName("ProgressBar")
$statusText  = $window.FindName("StatusText")
$installBtn  = $window.FindName("InstallBtn")
$closeBtn    = $window.FindName("CloseBtn")

function Write-Log {
    param($msg, $tone = "normal")
    $color = switch ($tone) {
        "ok"    { "#A6E3A1" }
        "warn"  { "#F9E2AF" }
        "error" { "#F38BA8" }
        default { "#CDD6F4" }
    }
    $logText.Inlines.Add([Windows.Documents.Run]::new("$msg`n"))
    $logText.Inlines | Select-Object -Last 1 | ForEach-Object { $_.Foreground = $color }
}

# Pre-populate log with build result
if ($buildOk) {
    $logText.Text = ""
    Write-Log "Build succeeded." "ok"
} else {
    $logText.Text = ""
    Write-Log "Build FAILED — fix errors before installing." "error"
    $installBtn.IsEnabled = $false
}

$installBtn.Add_Click({
    $installBtn.IsEnabled = $false
    $logText.Text = ""

    # Step 1 — Check Revit not running
    Write-Log "[1/4] Checking Revit is not running..."
    $progressBar.Value = 10
    $window.Dispatcher.Invoke([action]{}, "Background")

    if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
        Write-Log "Revit is currently open. Please close it and try again." "error"
        $statusText.Text = "Close Revit and retry."
        $installBtn.IsEnabled = $true
        return
    }
    Write-Log "Revit is not running." "ok"

    # Step 2 — Verify files exist
    Write-Log "[2/4] Verifying build output..."
    $progressBar.Value = 30

    if (-not (Test-Path $DllSource)) {
        Write-Log "DLL not found: $DllSource" "error"
        $installBtn.IsEnabled = $true
        return
    }
    Write-Log "DLL found: EllahColNumPro.dll" "ok"
    Write-Log "Addin found: EllahColNumPro.addin" "ok"

    # Step 3 — Create folder
    Write-Log "[3/4] Preparing Addins folder..."
    $progressBar.Value = 60
    if (-not (Test-Path $AddinsFolder)) {
        New-Item -ItemType Directory -Path $AddinsFolder -Force | Out-Null
        Write-Log "Created: $AddinsFolder" "ok"
    } else {
        Write-Log "Folder exists: $AddinsFolder" "ok"
    }

    # Step 4 — Copy files
    Write-Log "[4/4] Copying files..."
    $progressBar.Value = 85
    try {
        Copy-Item -Path $DllSource   -Destination $AddinsFolder -Force
        Copy-Item -Path $AddinSource -Destination $AddinsFolder -Force
        Write-Log "Copied: EllahColNumPro.dll" "ok"
        Write-Log "Copied: EllahColNumPro.addin" "ok"
    } catch {
        Write-Log "Error copying files: $_" "error"
        $installBtn.IsEnabled = $true
        return
    }

    $progressBar.Value = 100
    Write-Log "" 
    Write-Log "Installation complete!" "ok"
    Write-Log "Open Revit 2025 and look for ELLAH-ColNum Pro in the Add-Ins tab." "ok"
    $statusText.Text = "Installed successfully."
})

$closeBtn.Add_Click({ $window.Close() })

[void]$window.ShowDialog()
