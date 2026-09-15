using System.IO;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
namespace ResumeBuilder;

public partial class SettingsWindow : Window {
    AppSettings _s=Storage.LoadSettings();
    public SettingsWindow(){ InitializeComponent(); LoadFields(); RefreshInspector(); }
    void LoadFields(){ ResumeBox.Text=_s.OriginalResume; PromptBox.Text=_s.MasterPrompt; BaselineStatus.Text=File.Exists(BaselineProfileImporter.BaselineProfilePath) ? "Imported" : "Not imported"; IncomingBox.Text=_s.IncomingFolder; ImportedBox.Text=_s.ImportedFolder; RootBox.Text=_s.ResumeRootFolder; DocxBox.IsChecked=_s.Docx; PdfBox.IsChecked=_s.Pdf; AutoFillBox.IsChecked=_s.AutoFillComposer; AutoCaptureBox.IsChecked=_s.AutoCaptureResult; }
    string? PickFile(string filter){ var d=new Microsoft.Win32.OpenFileDialog{Filter=filter}; return d.ShowDialog()==true?d.FileName:null; }
    string? PickFolder(){ using var d=new Forms.FolderBrowserDialog(); return d.ShowDialog()==Forms.DialogResult.OK?d.SelectedPath:null; }
    void ResumeBrowse_Click(object s,RoutedEventArgs e){var p=PickFile("Resume files|*.docx;*.pdf|All files|*.*");if(p!=null)ResumeBox.Text=p;}
    void PromptBrowse_Click(object s,RoutedEventArgs e){var p=PickFile("Text files|*.txt;*.md|All files|*.*");if(p!=null)PromptBox.Text=p;}
    void IncomingBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)IncomingBox.Text=p;}
    void ImportedBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)ImportedBox.Text=p;}
    void RootBrowse_Click(object s,RoutedEventArgs e){var p=PickFolder();if(p!=null)RootBox.Text=p;}
    void Save_Click(object s,RoutedEventArgs e){
        if(!string.IsNullOrWhiteSpace(IncomingBox.Text)&&!Directory.Exists(IncomingBox.Text)){System.Windows.MessageBox.Show("Incoming folder does not exist.");return;}
        if(!string.IsNullOrWhiteSpace(ImportedBox.Text)&&!Directory.Exists(ImportedBox.Text)){System.Windows.MessageBox.Show("Imported folder does not exist.");return;}
        _s=new(){OriginalResume=ResumeBox.Text.Trim(),CandidateProfile=_s.CandidateProfile,MasterPrompt=PromptBox.Text.Trim(),IncomingFolder=IncomingBox.Text.Trim(),ImportedFolder=ImportedBox.Text.Trim(),ResumeRootFolder=RootBox.Text.Trim(),Docx=DocxBox.IsChecked==true,Pdf=PdfBox.IsChecked==true,AutoFillComposer=AutoFillBox.IsChecked==true,AutoCaptureResult=AutoCaptureBox.IsChecked==true};
        Storage.SaveSettings(_s); System.Windows.MessageBox.Show("Settings saved.");
    }

    void LoadSampleJobs_Click(object s, RoutedEventArgs e) {
        var settings=Storage.LoadSettings();
        if(string.IsNullOrWhiteSpace(settings.IncomingFolder)||!Directory.Exists(settings.IncomingFolder)){System.Windows.MessageBox.Show("Configure an existing Incoming folder first.");return;}
        var batch=new JobBatch{SchemaVersion="1.0",Source="sample",Jobs=new(){
            new(){JobId="SAMPLE-001",Company="Google",Title="Senior Software Engineer",Location="Mountain View, CA",Jd="Sample JD for a senior software engineer role."},
            new(){JobId="SAMPLE-002",Company="Stripe",Title="Backend Engineer",Location="Remote",Jd="Sample JD for a backend engineer role."},
            new(){JobId="SAMPLE-003",Company="Amazon",Title="Software Engineer II",Location="Seattle, WA",Jd="Sample JD for a software engineer role."}}};
        var file=Path.Combine(settings.IncomingFolder,$"sample-jobs-{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
        File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(batch,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        System.Windows.MessageBox.Show("Sample JobBatch v1 created in Incoming. Click Refresh Input on the main screen.");
    }
    void ClearTestQueue_Click(object s, RoutedEventArgs e) {
        var tasks=Storage.LoadTasks(); var n=tasks.RemoveAll(t=>t.JobId.StartsWith("SAMPLE-",StringComparison.OrdinalIgnoreCase)); Storage.SaveTasks(tasks);
        System.Windows.MessageBox.Show($"Removed {n} sample task(s). Restart A to refresh the visible queue.");
    }


    public void RefreshInspector() {
        if(PreparedInputBox is null) return;
        PreparedInputBox.Text=RequestPreparation.Load()?.Text ?? "No job has been prepared yet.";
    }


    void CopyPrepared_Click(object s, RoutedEventArgs e) {
        var prepared=RequestPreparation.Load();
        if(prepared is null){System.Windows.MessageBox.Show("No prepared input exists yet.");return;}
        CopyToClipboard(prepared.Text,"Prepared input");
    }

    /// <summary>
    /// Single copy path for this window. On failure it pre-selects the text box and points at the
    /// saved .txt file, so the user never has to hunt for the prepared input.
    /// </summary>
    void CopyToClipboard(string text,string label) {
        var clip=ClipboardService.SetText(text);
        if(clip.Success){ System.Windows.MessageBox.Show(label+" copied. "+clip.Message); return; }

        PreparedInputBox.Focus();
        PreparedInputBox.SelectAll();
        System.Windows.MessageBox.Show(
            clip.Message+Environment.NewLine+Environment.NewLine+
            "The text is already selected in Job Details — press Ctrl+C to copy it."+Environment.NewLine+
            "A plain-text copy is also saved at:"+Environment.NewLine+RequestPreparation.PreparedTextPath);
    }

    void ImportBaseline_Click(object s,RoutedEventArgs e) {
        try {
            BaselineProfileImporter.CreateBaselineFromDocx(ResumeBox.Text.Trim());
            BaselineStatus.Text="Imported";
            System.Windows.MessageBox.Show("Original resume text imported successfully. No resume facts were invented.");
        } catch(Exception ex) { System.Windows.MessageBox.Show("Baseline import failed:\n\n"+ex.Message); }
    }

    void PrepareProfileCreation_Click(object s,RoutedEventArgs e) {
        try {
            var request=BaselineProfileImporter.BuildProfileCreationRequest(PromptBox.Text.Trim());
            var prepared=new PreparedRequest{JobId="BASELINE",Company="",Title="Initial Candidate Profile",Text=request};
            RequestPreparation.Save(prepared);
            PreparedInputBox.Text=request;
            System.Windows.MessageBox.Show("Profile-creation request prepared. Review it in Job Details, then paste it into ChatGPT.");
            // Clipboard last: a clipboard problem must not make the preparation look like a failure.
            CopyToClipboard(request,"Profile-creation request");
        } catch(Exception ex) { System.Windows.MessageBox.Show("Could not prepare profile creation:\n\n"+ex.Message); }
    }


    void PasteResult_Click(object s,RoutedEventArgs e) {
        var text=ClipboardService.TryGetText();
        if(text is not null){ ResultInputBox.Text=text; ProfileStatus.Text="Pasted "+text.Length+" characters from the clipboard."; }
        else ProfileStatus.Text="The clipboard holds no text, or it stayed locked after several retries. Paste into the result box with Ctrl+V.";
    }

    void SaveProfile_Click(object s,RoutedEventArgs e) {
        try {
            var report=CandidateProfileStore.NormalizeAndSave(ResultInputBox.Text);
            var detail=report.Describe();
            ProfileStatus.Text="PASS — candidate-profile.json normalized, validated and saved."+Environment.NewLine+detail;
            System.Windows.MessageBox.Show("Candidate profile saved successfully."+Environment.NewLine+Environment.NewLine+detail);
        } catch(Exception ex) {
            ProfileStatus.Text="FAIL — "+ex.Message;
            System.Windows.MessageBox.Show("Profile could not be normalized:\n\n"+ex.Message);
        }
    }

}