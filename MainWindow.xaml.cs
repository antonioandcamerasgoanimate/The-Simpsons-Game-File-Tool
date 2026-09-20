using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TSGFileTool.Formats;
using TSGFileTool.Native;
using TSGFileTool.Xml;
using System.Collections.Generic;

namespace TSGFileTool;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private StocArchive? _str;
    private readonly NameDatabase _names = new();

    private byte[]? _itxdData;
    private List<ItxdTextureEntry> _itxdEntries = new();

    private byte[]? _vp6Data;
    private List<Chunk> _vp6Chunks = new();

    private byte[]? _rwData;
    private List<RwSection> _rwSections = new();
    private List<RwSection> _rwFlat = new();

    private byte[]? _rawData;
    private string? _rawPath;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _strSummary = "No archive loaded.";
    public string StrSummary { get => _strSummary; set => SetField(ref _strSummary, value); }

    private string _itxdSummary = "No texture file loaded.";
    public string ItxdSummary { get => _itxdSummary; set => SetField(ref _itxdSummary, value); }

    private string _vp6Summary = "No video loaded.";
    public string Vp6Summary { get => _vp6Summary; set => SetField(ref _vp6Summary, value); }

    private string _rwSummary = "No RenderWare stream loaded.";
    public string RwSummary { get => _rwSummary; set => SetField(ref _rwSummary, value); }



    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        field = value; Raise(name!); return true;
    }

    // ---------- STR tab ----------
    // Everything here operates on QfsAsset (StocArchive.DecodedAssets) - the
    // real, genuinely DECOMPRESSED sub-file list. EARS/QFS decompression is
    // solved (confirmed against the authoritative simpsons_str.bms QuickBMS
    // script), so Data here is ready to use as-is, not still-compressed
    // bytes like the earlier EarsSubFile-based version of this tab.

    private void OpenStr_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "STR archives (*.str)|*.str|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _str = StocArchive.Load(dlg.FileName);
            _names.Load(dlg.FileName);
            RefreshStrGrid();
            int failedEntries = _str.EntryDecodeErrors.Count(err => err != null);
            StrSummary = $"{Path.GetFileName(dlg.FileName)} - {_str.DecodedAssets.Count} real decoded assets " +
                         $"({_str.Entries.Count} TOC entries, {failedEntries} failed to decode), {_str.FileData.Length:N0} bytes";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to load .str", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshStrGrid()
    {
        if (_str == null) return;
        StrGrid.ItemsSource = _str.DecodedAssets.Select(a =>
        {
            var manual = _names.ResolveManualOnly(a.Index);
            return new StrEntryRow(a, manual);
        }).ToList();
    }

    private QfsAsset? SelectedSubFile() => (StrGrid.SelectedItem as StrEntryRow)?.Asset;

    private void StrGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (StrGrid.SelectedItem is StrEntryRow row)
            RenameBox.Text = row.RawName ?? row.Asset.Name;
    }

    private void ApplyName_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSubFile() is not { } asset) { Info("Select a sub-file first."); return; }
        _names.SetManualName(asset.Index, RenameBox.Text);
        RefreshStrGrid();
    }

    private void SaveNames_Click(object sender, RoutedEventArgs e)
    {
        if (_str == null) { Info("Load a .str first."); return; }
        _names.Save(_str.SourcePath);
        Info($"Saved names to:\n{NameDatabase.SidecarPathFor(_str.SourcePath)}\nThis loads automatically next time you open this .str.");
    }

    private void ExportStrMeta_Click(object sender, RoutedEventArgs e)
    {
        if (_str == null || SelectedSubFile() is not { } asset) { Info("Load a .str and select a sub-file first."); return; }
        var name = _names.ResolveManualOnly(asset.Index) ?? asset.Name;
        var dlg = new SaveFileDialog { FileName = name };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, asset.Data);
        var fields = new List<(string, string)>
        {
            ("Name", name), ("Tag", asset.Tag), ("BuildPath", asset.BuildPath),
            ("ParentTocEntryIndex", asset.ParentTocEntryIndex.ToString()), ("InnerOffset", asset.InnerOffset.ToString())
        };
        MetaXmlExporter.Write(dlg.FileName, dlg.FileName + ".meta.xml", "QfsAsset", asset.Data, fields);
        Info("Wrote the .bin plus a matching .meta.xml sidecar.");
    }

    private void ExtractSelectedStr_Click(object sender, RoutedEventArgs e)
    {
        if (_str == null || SelectedSubFile() is not { } asset) { Info("Load a .str and select a sub-file first."); return; }
        var name = _names.ResolveManualOnly(asset.Index) ?? asset.Name;
        var dlg = new SaveFileDialog { FileName = name };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, asset.Data);
        Info($"Wrote {asset.Data.Length:N0} bytes to:\n{dlg.FileName}");
    }

    private void ExtractAllStr_Click(object sender, RoutedEventArgs e)
    {
        if (_str == null) { Info("Load a .str first."); return; }
        var dlg = new OpenFolderDialog { Title = "Choose output folder" };
        if (dlg.ShowDialog() != true) return;
        _str.ExtractAllTo(dlg.FolderName);
        int failedEntries = _str.EntryDecodeErrors.Count(err => err != null);
        Info($"Extracted {_str.DecodedAssets.Count} real decoded assets + manifest.xml to:\n{dlg.FolderName}" +
             (failedEntries > 0 ? $"\n\n{failedEntries} TOC entries failed to decode - see manifest.xml's decodeError attributes for details." : ""));
    }

    /// <summary>Sends the selected asset's already-decompressed bytes straight to the
    /// Texture tab if it looks like an .itxd - a real, working shortcut now that EARS
    /// decompression produces genuinely valid ITXD data.</summary>
    private void OpenSelectedInTextureTab_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSubFile() is not { } asset) { Info("Select a sub-file first."); return; }
        if (!ItxdTexture.LooksLikeItxd(asset.Data))
        {
            Info("The selected asset doesn't look like an .itxd");
            return;
        }
        try
        {
            _itxdData = asset.Data;
            _itxdEntries = ItxdTexture.Parse(_itxdData);
            ItxdGrid.ItemsSource = _itxdEntries;
            ItxdSummary = $"{asset.Name} (from .str) - {_itxdEntries.Count} textures, {_itxdData.Length:N0} bytes";
            TexturePreview.Source = null;
            MainTabs.SelectedIndex = 1; // Texture tab
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to parse as .itxd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Replaces the selected asset's parent TOC entry with new decompressed
    /// content, recompresses it with the validated RefPack encoder, and rebuilds the
    /// in-memory archive. See the STR tab's info text for the entry-vs-sub-file
    /// scoping this operates at.</summary>
    private void ReplaceTocEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_str == null || SelectedSubFile() is not { } asset) { Info("Load a .str and select a sub-file first."); return; }
        var dlg = new OpenFileDialog { Title = $"Pick replacement decompressed data for TOC entry {asset.ParentTocEntryIndex}" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var newData = File.ReadAllBytes(dlg.FileName);
            _str.ReplaceTocEntry(asset.ParentTocEntryIndex, newData);
            RefreshStrGrid();
            Info($"TOC entry {asset.ParentTocEntryIndex} replaced and the archive rebuilt in memory " +
                 "(other entries keep their original compressed bytes untouched). Use 'Save Archive As...' to write it out.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Replace failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveStrAs_Click(object sender, RoutedEventArgs e)
    {
        if (_str == null) { Info("Load a .str first."); return; }
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(_str.SourcePath), Filter = "STR archives (*.str)|*.str" };
        if (dlg.ShowDialog() != true) return;
        _str.Save(dlg.FileName);
        Info("Saved.");
    }

    // ---------- Texture (.itxd) tab ----------

    private void OpenItxd_Click(object sender, RoutedEventArgs e)

    {
        var dlg = new OpenFileDialog { Filter = "ITXD textures (*.itxd)|*.itxd|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _itxdData = File.ReadAllBytes(dlg.FileName);
            _itxdEntries = ItxdTexture.Parse(_itxdData);
            ItxdGrid.ItemsSource = _itxdEntries;
            ItxdSummary = $"{Path.GetFileName(dlg.FileName)} - {_itxdEntries.Count} textures, {_itxdData.Length:N0} bytes";
            TexturePreview.Source = null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to load .itxd", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ItxdGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => PreviewTexture_Click(sender, e);

    private void PreviewTexture_Click(object sender, RoutedEventArgs e)
    {
        if (_itxdData == null || ItxdGrid.SelectedItem is not ItxdTextureEntry entry) return;
        try
        {
            var bgra = ItxdTexture.DecodeToBgra32(_itxdData, entry, UntileCheck.IsChecked == true);
            int w = Math.Max(1, entry.Width), h = Math.Max(1, entry.Height);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            TexturePreview.Source = bmp;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't decode this texture:\n" + ex.Message, "Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportTexturePng_Click(object sender, RoutedEventArgs e)
    {
        if (_itxdData == null || ItxdGrid.SelectedItem is not ItxdTextureEntry entry) { Info("Load a .itxd and select a texture first."); return; }
        var dlg = new SaveFileDialog { FileName = (string.IsNullOrWhiteSpace(entry.Name) ? "texture" : entry.Name) + ".png" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bgra = ItxdTexture.DecodeToBgra32(_itxdData, entry, UntileCheck.IsChecked == true);
            int w = Math.Max(1, entry.Width), h = Math.Max(1, entry.Height);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(dlg.FileName);
            encoder.Save(fs);
            Info("Exported PNG.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't export this texture:\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportTextureRaw_Click(object sender, RoutedEventArgs e)
    {
        if (_itxdData == null || ItxdGrid.SelectedItem is not ItxdTextureEntry entry) { Info("Load a .itxd and select a texture first."); return; }
        var dlg = new SaveFileDialog { FileName = (string.IsNullOrWhiteSpace(entry.Name) ? "texture" : entry.Name) + ".bin" };
        if (dlg.ShowDialog() != true) return;
        var raw = _itxdData.AsSpan((int)entry.DataOffset, (int)entry.DataSize).ToArray();
        File.WriteAllBytes(dlg.FileName, raw);
        MetaXmlExporter.Write(dlg.FileName, dlg.FileName + ".meta.xml", "ItxdTexture", raw, new[]
        {
            ("Name", entry.Name), ("Width", entry.Width.ToString()), ("Height", entry.Height.ToString()),
            ("Format", entry.FormatName), ("DataOffset", entry.DataOffset.ToString())
        });
        Info("Wrote raw tiled texture data + meta.xml.");
    }

    /// <summary>
    /// Import PNG, same pixel size as the selected texture only. This was
    /// referenced by MainWindow.xaml's "Import PNG into Selected (same
    /// size)..." button but the method itself was missing - a real,
    /// build-breaking bug (a WPF x:Class Click handler that doesn't exist in
    /// code-behind fails compilation), which is almost certainly why
    /// nothing on this tab - or anywhere else - appeared to work: the whole
    /// app couldn't build. Fixed by actually implementing both this and
    /// ExportItxdFile_Click below.
    /// </summary>
    private void ImportTexturePng_Click(object sender, RoutedEventArgs e)
    {
        if (_itxdData == null || ItxdGrid.SelectedItem is not ItxdTextureEntry entry) { Info("Load a .itxd and select a texture first."); return; }
        var dlg = new OpenFileDialog { Filter = "PNG images (*.png)|*.png" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bgra = LoadPngAsBgra32(dlg.FileName, out int w, out int h);
            if (w != entry.Width || h != entry.Height)
            {
                MessageBox.Show(this,
                    $"This PNG is {w}x{h} but the selected texture is {entry.Width}x{entry.Height}. " +
                    "Use 'Import PNG (resize)' instead if you want it scaled to fit automatically.",
                    "Size mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ImportBgraIntoTexture(bgra, entry);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Import failed:\n" + ex.Message, "Import PNG", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Import PNG, scaled to fit the selected texture's exact width/height
    /// (the .itxd format's fixed-size slots mean the SLOT can't resize, only the
    /// source image gets resized to match it).</summary>
    private void ImportTexturePngResize_Click(object sender, RoutedEventArgs e)
    {
        if (_itxdData == null || ItxdGrid.SelectedItem is not ItxdTextureEntry entry) { Info("Load a .itxd and select a texture first."); return; }
        var dlg = new OpenFileDialog { Filter = "PNG images (*.png)|*.png" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var bgra = LoadPngAsBgra32(dlg.FileName, out int w, out int h, resizeTo: (entry.Width, entry.Height));
            ImportBgraIntoTexture(bgra, entry);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Import failed:\n" + ex.Message, "Import PNG (resize)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportBgraIntoTexture(byte[] bgra, ItxdTextureEntry entry)
    {
        var encoded = ItxdTexture.EncodeFromBgra32(bgra!, entry, UntileCheck.IsChecked == true);
        ItxdTexture.ReplaceTexture(_itxdData!, entry, encoded);
        PreviewTexture_Click(this, new RoutedEventArgs());
        Info("Imported into memory. Use 'Export Whole .itxd As...' to write the modified file out.");
    }

    private static byte[] LoadPngAsBgra32(string path, out int width, out int height, (int w, int h)? resizeTo = null)
    {
        // Was `new PngBitmapDecoder(new Uri(path), ...)` - constructing the
        // decoder from a bare Uri is a known WPF gotcha ("El codec no puede
        // usar el tipo de URI proporcionado" / "the codec cannot use the
        // provided URI type") that fails unpredictably depending on the
        // exact path. A FileStream is the robust, standard way to load a
        // local file into a BitmapDecoder.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        if (resizeTo is { } target && (frame.PixelWidth != target.w || frame.PixelHeight != target.h))
        {
            var scaleX = (double)target.w / frame.PixelWidth;
            var scaleY = (double)target.h / frame.PixelHeight;
            frame = new TransformedBitmap(frame, new ScaleTransform(scaleX, scaleY));
        }

        width = frame.PixelWidth; height = frame.PixelHeight;
        var bytes = new byte[width * height * 4];
        frame.CopyPixels(bytes, width * 4, 0);
        return bytes;
    }

    /// <summary>Writes the whole in-memory .itxd (with any imports applied) to a new file
    /// - was referenced by the "Export Whole .itxd As..." button but never implemented.</summary>
    private void ExportItxdFile_Click(object sender, RoutedEventArgs e)
    {
        if (_itxdData == null) { Info("Load a .itxd first."); return; }
        var dlg = new SaveFileDialog { FileName = "modified.itxd", Filter = "ITXD textures (*.itxd)|*.itxd" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, _itxdData);
        Info("Saved.");
    }

    // ---------- VP6 tab ----------

    private void OpenVp6_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "VP6 video (*.vp6)|*.vp6|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        _vp6Data = File.ReadAllBytes(dlg.FileName);
        _vp6Chunks = ChunkScanner.Scan(_vp6Data);
        Vp6Grid.ItemsSource = _vp6Chunks.Select((c, i) => new Vp6ChunkRow(i, c)).ToList();
        Vp6Summary = $"{Path.GetFileName(dlg.FileName)} - {_vp6Chunks.Count} chunks, {_vp6Data.Length:N0} bytes";
    }

    private void ExtractSelectedChunk_Click(object sender, RoutedEventArgs e)
    {
        if (_vp6Data == null || Vp6Grid.SelectedItem is not Vp6ChunkRow row) { Info("Load a .vp6 and select a chunk first."); return; }
        var dlg = new SaveFileDialog { FileName = $"{row.Tag}_{row.Index}.bin" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, ChunkScanner.ExtractPayload(_vp6Data, row.Source));
        Info($"Wrote {row.PayloadSize:N0} bytes.");
    }

    private void ExtractAllChunks_Click(object sender, RoutedEventArgs e)
    {
        if (_vp6Data == null) { Info("Load a .vp6 first."); return; }
        var dlg = new OpenFolderDialog { Title = "Choose output folder" };
        if (dlg.ShowDialog() != true) return;
        for (int i = 0; i < _vp6Chunks.Count; i++)
        {
            var c = _vp6Chunks[i];
            File.WriteAllBytes(Path.Combine(dlg.FolderName, $"{i:D3}_{c.Tag}.bin"), ChunkScanner.ExtractPayload(_vp6Data, c));
        }
        Vp6Manifest.Write(_vp6Chunks, Path.Combine(dlg.FolderName, "index.xml"));
        Info($"Extracted {_vp6Chunks.Count} chunks + index.xml to:\n{dlg.FolderName}");
    }

    private void ExportVp6Meta_Click(object sender, RoutedEventArgs e)
    {
        if (_vp6Data == null) { Info("Load a .vp6 first."); return; }
        var dlg = new SaveFileDialog { FileName = "video.bin" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, _vp6Data);
        var mvhd = _vp6Chunks.FirstOrDefault(c => c.Tag == "MVhd");
        var fields = new List<(string, string)> { ("ChunkCount", _vp6Chunks.Count.ToString()) };
        if (mvhd != null)
        {
            var payload = ChunkScanner.ExtractPayload(_vp6Data, mvhd);
            if (payload.Length >= 16)
            {
                string codec = Encoding.ASCII.GetString(payload, 0, 4);
                uint w = BitConverter.ToUInt32(payload, 4), h = BitConverter.ToUInt32(payload, 8), frames = BitConverter.ToUInt32(payload, 12);
                fields.Add(("Codec", codec)); fields.Add(("Width", w.ToString())); fields.Add(("Height", h.ToString())); fields.Add(("FrameCount", frames.ToString()));
            }
        }
        MetaXmlExporter.Write(dlg.FileName, dlg.FileName + ".meta.xml", "SchlVideo", _vp6Data, fields);
        Info("Wrote the .bin plus a meta.xml with whatever MVhd header fields were found.");
    }

    // ---------- RenderWare tab ----------

    private void OpenRw_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "RenderWare streams (*.dff;*.rws;*.txd; *.itxd; *.XEN; *.PS3; *.preinstanced)|*.dff;*.rws;*.txd; *.itxd; *.XEN; *.PS3; *.preinstanced|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _rwData = File.ReadAllBytes(dlg.FileName);
            _rwSections = RenderWareStream.Parse(_rwData);
            _rwFlat = RenderWareStream.Flatten(_rwSections).ToList();
            RwGrid.ItemsSource = _rwFlat.Select((s, i) => new RwSectionRow(i, s)).ToList();
            RwSummary = $"{Path.GetFileName(dlg.FileName)} - {_rwFlat.Count} sections, {_rwData.Length:N0} bytes";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Failed to load RenderWare stream", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExtractRwSection_Click(object sender, RoutedEventArgs e)
    {
        if (_rwData == null || RwGrid.SelectedItem is not RwSectionRow row) { Info("Load a RenderWare stream and select a section first."); return; }
        var dlg = new SaveFileDialog { FileName = $"{row.Index:D3}_{row.Source.TypeName.Replace(" ", "")}.bin" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, RenderWareStream.ExtractPayload(_rwData, row.Source));
        Info($"Wrote {row.Size:N0} bytes.");
    }

    private void ExtractAllRw_Click(object sender, RoutedEventArgs e)
    {
        if (_rwData == null) { Info("Load a RenderWare stream first."); return; }
        var dlg = new OpenFolderDialog { Title = "Choose output folder" };
        if (dlg.ShowDialog() != true) return;
        for (int i = 0; i < _rwFlat.Count; i++)
        {
            var s = _rwFlat[i];
            var safe = s.TypeName.Replace(" ", "").Replace("(", "").Replace(")", "");
            File.WriteAllBytes(Path.Combine(dlg.FolderName, $"{i:D3}_{safe}.bin"), RenderWareStream.ExtractPayload(_rwData, s));
        }
        RwManifest.Write(_rwFlat, Path.Combine(dlg.FolderName, "index.xml"));
        Info($"Extracted {_rwFlat.Count} sections + index.xml to:\n{dlg.FolderName}");
    }

    // ---------- Raw / Other Files tab ----------

    private void OpenSnuMus_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        _rawPath = dlg.FileName;
        _rawData = File.ReadAllBytes(dlg.FileName);
        HexView.Text = HexDump(_rawData, 0, Math.Min(_rawData.Length, 8192));
        ManualLength.Text = Math.Min(_rawData.Length, 4096).ToString();
    }

    private void ProbeFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        Info(FormatProbe.Probe(_rawData));
    }

    private void ExtractManualRange_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        if (!TryGetRange(out int off, out int len)) return;
        var dlg = new SaveFileDialog { FileName = "range.bin" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, _rawData.AsSpan(off, len).ToArray());
        Info($"Wrote {len:N0} bytes from offset {off}.");
    }

    private void DecompressManualRange_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        if (!TryGetRange(out int off, out int len)) return;
        try
        {
            var slice = _rawData.AsSpan(off, len).ToArray();
            var outBytes = XCompressNative.TryDecompressLzx(slice, (uint)len * 4);
            var dlg = new SaveFileDialog { FileName = "range_decompressed.bin" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllBytes(dlg.FileName, outBytes);
            Info($"Decompressed {len:N0} -> {outBytes.Length:N0} bytes.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "LZX decompression failed for this range:\n" + ex.Message,
                "Decompress", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Pure C# LZX decode (LzxDecoder.cs, ported from the mspack source you
    /// supplied) - no xcompress.dll/P-Invoke/Windows-x86 requirement. Asks
    /// for the expected decompressed size up front because the LZX format
    /// needs to know how many bytes to decode (there's no end marker).
    /// Real, tested code - but see Scope/Read Me: it did not decode this
    /// game's EARS-compressed payloads in extensive testing.
    /// </summary>
    private void DecompressManualRangeManaged_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        if (!TryGetRange(out int off, out int len)) return;

        // LZX has no end-of-stream marker, so it needs to be told how many
        // bytes to produce. Same default heuristic as the xcompress.dll
        // button (4x the compressed range) - adjust ManualLength/retry if
        // that's not enough for a particular block.
        int expectedSize = len * 4;

        var slice = _rawData.AsSpan(off, len).ToArray();
        var xbox = LzxDecoder.DecompressXbox(slice, expectedSize, out var xboxError);
        if (xbox != null) { SaveManagedLzxResult(xbox, "Xbox-framed"); return; }

        var raw = LzxDecoder.DecompressRaw(slice, expectedSize, out var rawError);
        if (raw != null) { SaveManagedLzxResult(raw, "raw/unframed"); return; }

        MessageBox.Show(this,
            "Managed LZX decompression failed in both Xbox-framed and raw/unframed modes for this range.\n" +
            $"Xbox-framed error: {xboxError ?? "(no detail)"}\nRaw/unframed error: {rawError ?? "(no detail)"}\n\n" +
            "This is the same real decoder used elsewhere in the tool - it's a genuine failure, not a placeholder.",
            "Decompress", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SaveManagedLzxResult(byte[] outBytes, string mode)
    {
        var dlg = new SaveFileDialog { FileName = "range_decompressed_managed.bin" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, outBytes);
        Info($"Decompressed ({mode}) -> {outBytes.Length:N0} bytes.");
    }

    private void DecompressRefPackRange_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        if (!TryGetRange(out int off, out int len)) return;
        var slice = _rawData.AsSpan(off, len).ToArray();
        if (!RefPackDecoder.IsRefPack(slice))
        {
            Info("No RefPack signature (0x10FB/0x90FB) at the start of this range.");
            return;
        }
        try
        {
            var outBytes = RefPackDecoder.Decompress(slice);
            var dlg = new SaveFileDialog { FileName = "range_decompressed_refpack.bin" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllBytes(dlg.FileName, outBytes);
            Info($"Decompressed {len:N0} -> {outBytes.Length:N0} bytes.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "RefPack decompression failed:\n" + ex.Message, "Decompress", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DecompressZlibRange_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        if (!TryGetRange(out int off, out int len)) return;
        var slice = _rawData.AsSpan(off, len).ToArray();
        if (!ZlibDecoder.LooksLikeZlib(slice))
        {
            Info("No zlib header (0x78 + a standard FLG byte) at the start of this range.");
            return;
        }
        try
        {
            var outBytes = ZlibDecoder.Decompress(slice);
            var dlg = new SaveFileDialog { FileName = "range_decompressed_zlib.bin" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllBytes(dlg.FileName, outBytes);
            Info($"Decompressed {len:N0} -> {outBytes.Length:N0} bytes.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Zlib decompression failed:\n" + ex.Message, "Decompress", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Scans the WHOLE currently-open file for genuine RefPack (0x10FB/
    /// 0x90FB) and zlib header byte patterns, so you can see every candidate
    /// at once instead of guessing offsets by hand - this is the same
    /// approach used to investigate zone01_split4.itxd (found 3 RefPack-
    /// looking positions, all of which turned out to be coincidental when
    /// actually decompressed; zero zlib headers anywhere in that file).
    /// </summary>
    private void ScanCodecHeaders_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null) { Info("Open a file first."); return; }
        var refpackHits = new List<int>();
        for (int i = 0; i < _rawData.Length - 1; i++)
            if (RefPackDecoder.IsRefPack(_rawData, i)) refpackHits.Add(i);
        var zlibHits = ZlibDecoder.FindHeaders(_rawData);

        var msg = $"RefPack signature matches: {refpackHits.Count}" +
                  (refpackHits.Count > 0 ? " at offsets " + string.Join(", ", refpackHits.Take(20)) : "") +
                  $"\nZlib header matches: {zlibHits.Count}" +
                  (zlibHits.Count > 0 ? " at offsets " + string.Join(", ", zlibHits.Take(20)) : "") +
                  "\n\nA match here is only a byte-pattern coincidence until you actually try decompressing at that offset - use the buttons above with that offset entered.";
        Info(msg);
    }

    private void ImportWholeFile_Click(object sender, RoutedEventArgs e)
    {
        if (_rawPath == null) { Info("Open a file first (this replaces the currently opened file's bytes in memory)."); return; }
        var dlg = new OpenFileDialog { Title = "Pick replacement data for the whole file" };
        if (dlg.ShowDialog() != true) return;
        _rawData = File.ReadAllBytes(dlg.FileName);
        HexView.Text = HexDump(_rawData, 0, Math.Min(_rawData.Length, 8192));
        var save = new SaveFileDialog { FileName = Path.GetFileName(_rawPath) };
        if (save.ShowDialog() == true)
        {
            File.WriteAllBytes(save.FileName, _rawData);
            Info("Saved replacement file.");
        }
    }

    private void ExportRawMeta_Click(object sender, RoutedEventArgs e)
    {
        if (_rawData == null || _rawPath == null) { Info("Open a file first."); return; }
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(_rawPath) };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, _rawData);
        MetaXmlExporter.Write(dlg.FileName, dlg.FileName + ".meta.xml", "RawFile", _rawData, new[]
        {
            ("SourceExtension", Path.GetExtension(_rawPath)), ("Probe", FormatProbe.Probe(_rawData))
        });
        Info("Wrote the file plus a meta.xml sidecar (includes the format probe result).");
    }

    private bool TryGetRange(out int off, out int len)
    {
        off = 0; len = 0;
        if (!int.TryParse(ManualOffset.Text, out off) || !int.TryParse(ManualLength.Text, out len) ||
            off < 0 || len <= 0 || _rawData == null || off + len > _rawData.Length)
        {
            Info("Enter a valid offset/length within the file.");
            return false;
        }
        return true;
    }

    private static string HexDump(byte[] data, int start, int count)
    {
        var sb = new StringBuilder();
        for (int i = start; i < start + count; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            int rowLen = Math.Min(16, start + count - i);
            for (int j = 0; j < 16; j++)
                sb.Append(j < rowLen ? data[i + j].ToString("X2") + " " : "   ");
            sb.Append(" ");
            for (int j = 0; j < rowLen; j++)
            {
                byte b = data[i + j];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Info(string msg) => MessageBox.Show(this, msg, "TSG File Tool", MessageBoxButton.OK, MessageBoxImage.Information);
}

public sealed class StrEntryRow
{
    public StrEntryRow(QfsAsset asset, string? manualName)
    {
        Asset = asset; RawName = manualName;
    }
    public QfsAsset Asset { get; }
    public string? RawName { get; }
    public int Index => Asset.Index;
    public string Name => RawName ?? Asset.Name;
    public string Tag => Asset.Tag;
    public int Size => Asset.Data.Length;
    public int ParentTocEntryIndex => Asset.ParentTocEntryIndex;
    public string BuildPath => Asset.BuildPath;
}

public sealed class Vp6ChunkRow
{
    public Vp6ChunkRow(int index, Chunk c) { Index = index; Source = c; }
    public int Index { get; }
    public Chunk Source { get; }
    public string Tag => Source.Tag;
    public long Offset => Source.Offset;
    public int PayloadSize => Source.PayloadSize;
}

public sealed class RwSectionRow
{
    public RwSectionRow(int index, RwSection s) { Index = index; Source = s; }
    public int Index { get; }
    public RwSection Source { get; }
    public string Indented => new string(' ', Source.Depth * 2) + Source.TypeName;
    public long Offset => Source.HeaderOffset;
    public uint Size => Source.Size;
    public string LibraryIdHex => $"0x{Source.LibraryId:X8}";
}
