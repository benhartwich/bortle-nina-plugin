using System.ComponentModel.Composition;
using System.Windows;

namespace Bortle.NinaPlugin.Sequencer;

[Export(typeof(ResourceDictionary))]
public partial class Templates : ResourceDictionary
{
    public Templates()
    {
        InitializeComponent();
    }
}
