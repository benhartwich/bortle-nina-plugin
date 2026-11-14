using System.ComponentModel.Composition;
using System.Windows;

namespace Bortle.NinaPlugin;

[Export(typeof(ResourceDictionary))]
public partial class LivestreamPanelTemplate : ResourceDictionary
{
    public LivestreamPanelTemplate()
    {
        InitializeComponent();
    }
}
