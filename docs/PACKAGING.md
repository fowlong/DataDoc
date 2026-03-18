# MSIX Packaging & Microsoft Store Submission

## Prerequisites

- Visual Studio 2022 with "Windows application development" workload
- Windows App SDK (if using MSIX)
- A Microsoft Partner Center developer account

## Building the MSIX Package

### Option 1: Visual Studio

1. Right-click the `CaptureFlow.App` project → **Publish** → **MSIX Package**
2. Create a self-signed certificate or use a trusted certificate
3. Set version, display name, and package identity
4. Build the package

### Option 2: Command Line

```bash
dotnet publish src/CaptureFlow.App/CaptureFlow.App.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=false
```

Then use `MakeAppx.exe` from the Windows SDK to create the MSIX package.

### Option 3: WAP (Windows Application Packaging Project)

Add a WAP project to the solution for more control:

1. Add → New Project → "Windows Application Packaging Project"
2. Set `CaptureFlow.App` as the entry point
3. Configure `Package.appxmanifest`
4. Build MSIX from the WAP project

## Package.appxmanifest Configuration

```xml
<Package>
  <Identity Name="CaptureFlowCSV"
            Publisher="CN=YourPublisher"
            Version="1.0.0.0" />
  <Properties>
    <DisplayName>CaptureFlow CSV</DisplayName>
    <PublisherDisplayName>Your Company</PublisherDisplayName>
    <Description>Document to CSV extraction and CSV to document merge generation.</Description>
  </Properties>
  <Applications>
    <Application Id="CaptureFlow"
                 Executable="CaptureFlow.exe"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="CaptureFlow CSV"
        Description="Extract data from documents to CSV"
        BackgroundColor="#2563EB"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

## Store Submission Checklist

- [ ] App icons at all required sizes (44x44, 71x71, 150x150, 310x310)
- [ ] Screenshots (at least 1 at 1366x768 or larger)
- [ ] Privacy policy URL
- [ ] Age rating questionnaire completed
- [ ] Test on clean Windows 10/11 install
- [ ] Verify no admin rights required
- [ ] Verify no unsigned DLLs
- [ ] Sign with trusted certificate (not self-signed for Store)
- [ ] Package passes Windows App Certification Kit (WACK)

## File Associations (Optional)

Register for document types in the manifest to allow "Open with CaptureFlow CSV":

```xml
<uap:Extension Category="windows.fileTypeAssociation">
  <uap:FileTypeAssociation Name="captureflow">
    <uap:SupportedFileTypes>
      <uap:FileType>.pdf</uap:FileType>
      <uap:FileType>.docx</uap:FileType>
    </uap:SupportedFileTypes>
  </uap:FileTypeAssociation>
</uap:Extension>
```

## Tesseract OCR Bundling

Tesseract data files (`tessdata/eng.traineddata`) must be included in the package:

1. Add `tessdata` folder to the project
2. Set files as "Content" with "Copy if newer"
3. Configure Tesseract engine to look in the app's directory

Alternatively, download on first use to keep the initial package smaller.

## Runtime Requirements

- .NET 8 Desktop Runtime (can be bundled as self-contained)
- Windows 10 version 1809 or later
- No admin rights required
- No external services required
