using System.ComponentModel.Composition;
using System.Windows;

namespace Bortle.NinaPlugin;

[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary
{
    public Options()
    {
        InitializeComponent();
    }
}
