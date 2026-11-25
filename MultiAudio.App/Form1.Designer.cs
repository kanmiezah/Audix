namespace MultiAudio.App;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        // Every size in Form1 is scaled explicitly with S(); framework autoscaling would double it.
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
        this.Text = "Audix";
    }
}
