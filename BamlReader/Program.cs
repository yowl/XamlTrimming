// See https://aka.ms/new-console-template for more information

using ICSharpCode.BamlDecompiler;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Settings;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Util;
using System.Text;
using System.Xml;
using ICSharpCode.Decompiler.CSharp.Syntax;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using ICSharpCode.Decompiler.TypeSystem;
using System.Runtime.Intrinsics.X86;
using System;
using CommandLine;
using static System.Net.WebRequestMethods;
using System.Linq;
using System.Windows.Markup;
using ICSharpCode.BamlDecompiler.Xaml;
using Attribute = System.Attribute;

namespace BamlReader;


public class Options
{
    readonly IEnumerable<string> files;
    readonly IEnumerable<string> searchDirectories;
    readonly IEnumerable<string> references;
    readonly IEnumerable<string> excludedAssemblies;

    public Options(IEnumerable<string> searchDirectories, IEnumerable<string> files, IEnumerable<string> references, IEnumerable<string> excludedAssemblies)
    {
        this.searchDirectories = searchDirectories;
        this.files = files;
        this.references = references;
        this.excludedAssemblies = excludedAssemblies;
    }

    [Option(shortName: 'd', HelpText = "Additional search directories")]
    public IEnumerable<string> SearchDirectories { get { return searchDirectories; } }

    [Option(shortName: 'f')]
    public IEnumerable<string> Files { get { return files; } }

    [Option(shortName: 'r')]
    public IEnumerable<string> References { get { return references; } }

    [Option(shortName: 'e')]
    public IEnumerable<string> ExcludedAssemblies { get { return excludedAssemblies; } }

}

internal struct AssemblyAndType
{
    public string AssemblyShortName { get; set; }
    public string TypeFullName { get; set; }
}


public class Program
{
    private static IEnumerable<string> referenceAssemblies;
    private static readonly HashSet<TrimmerType> trimmerTypes = new();
    private static readonly HashSet<TrimmerProperty> trimmerProperties = new();
    private static readonly HashSet<TrimmerMethod> trimmerMethods = new();
    static Dictionary<string, List<XmlnsDefinitionAttribute>> xmlnsDefinitions = new ();

    private static List<Assembly> refAssemblies = new();
    private static IEnumerable<string> searchDirectories;
    public static void Main(string[] args)
    {

        var result = new Parser(settings =>
        {
            settings.AllowMultiInstance = true;
        }).ParseArguments<Options>(args);
        referenceAssemblies = result.Value.References;
        searchDirectories = result.Value.SearchDirectories;
        Options = result.Value;

        ILSpySettings.SettingsFilePathProvider = new DefaultSettingsFilePathProvider("s.txt");
        var manager = new AssemblyListManager(ILSpySettings.Load());
        var list = manager.CreateDefaultList("l");
        AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
        foreach (var re in referenceAssemblies)
        {
            var reAss = Assembly.LoadFrom(re);
            var t = reAss.GetCustomAttributes();
            //== "System.Windows.Markup.XmlnsDefinitionAttribute"
            foreach (var nsAtt in reAss.GetCustomAttributes(typeof(XmlnsDefinitionAttribute)).Cast<XmlnsDefinitionAttribute>())
            {
                if (!xmlnsDefinitions.TryGetValue(nsAtt.XmlNamespace, out var attList))
                {
                    attList = new List<XmlnsDefinitionAttribute>();
                    xmlnsDefinitions[nsAtt.XmlNamespace] = attList;
                }
                attList.Add(nsAtt);
            }

            refAssemblies.Add(reAss);
        }

        foreach (var path in result.Value.Files)
        {
            var asm = new LoadedAssembly(list, path);
            var loadResult = asm.GetLoadResultAsync().GetAwaiter().GetResult();
            if (loadResult.MetadataFile != null)
            {
                switch (loadResult.MetadataFile.Kind)
                {
                    case MetadataFile.MetadataFileKind.PortableExecutable:
                    case MetadataFile.MetadataFileKind.WebCIL:
                        var module = loadResult.MetadataFile;
                        foreach (var resource in module.Resources)
                        {
                            if (resource.Name.EndsWith(".resources"))
                            {
                                AddTypesAndMethods(resource, asm);
                            }
                        }

                        break;
                    default:
                        break;
                }
            }

        }

        var writer = new XmlTextWriter("XamlRoots.xml", Encoding.UTF8);

        writer.WriteStartElement("linker");

        foreach (var assTypes in trimmerTypes.GroupBy(t => t.Assembly))
        {
            writer.WriteStartElement("assembly");

            writer.WriteStartAttribute("fullname");
            writer.WriteString(assTypes.Key);
            writer.WriteEndAttribute();

            List<TrimmerProperty> added = new();

            foreach (var ty in assTypes)
            {
                writer.WriteStartElement("type");

                writer.WriteStartAttribute("fullname");
                writer.WriteString(ty.TypeFullName);
                writer.WriteEndAttribute();

                foreach (var type in trimmerProperties.Where(t =>
                             t.Assembly == ty.Assembly && t.TypeFullName == ty.TypeFullName))
                {
                    writer.WriteStartElement("property");
                    writer.WriteStartAttribute("accessors");
                    writer.WriteString("all");
                    writer.WriteEndAttribute();

                    // just write the name, avoid having to get the correct type
                    writer.WriteStartAttribute("name");
                    writer.WriteString(type.Name);
                    writer.WriteEndAttribute();

                    writer.WriteEndElement();

                    writer.WriteString(Environment.NewLine);

                    added.Add(type);
                }

                foreach (var a in added)
                {
                    trimmerProperties.Remove(a);
                }

                foreach (var type in trimmerMethods.Where(t =>
                             t.Assembly == ty.Assembly && t.TypeFullName == ty.TypeFullName))
                {
                    writer.WriteStartElement("method");

                    // just write the name, avoid having to get the correct type
                    writer.WriteStartAttribute("name");
                    writer.WriteString(type.Name);
                    writer.WriteEndAttribute();

                    writer.WriteEndElement();

                    writer.WriteString(Environment.NewLine);
                }

                AddCtor(writer);

                writer.WriteEndElement(); //type
                writer.WriteString(Environment.NewLine);
            }

            writer.WriteEndElement(); //assembly
            writer.WriteString(Environment.NewLine);
        }

        foreach (var propAssemblies in trimmerProperties.GroupBy(p => p.Assembly))
        {
            writer.WriteStartElement("assembly");

            writer.WriteStartAttribute("fullname");
            writer.WriteString(propAssemblies.Key);
            writer.WriteEndAttribute();
            writer.WriteString(Environment.NewLine);

            foreach (var propType in propAssemblies.GroupBy(pa => pa.TypeFullName))
            {
                writer.WriteStartElement("type");

                writer.WriteStartAttribute("fullname");
                writer.WriteString(propType.Key);
                writer.WriteEndAttribute();
                writer.WriteString(Environment.NewLine);

                foreach(var prop in propType)
                {
                    writer.WriteStartElement("property");
                    writer.WriteStartAttribute("accessors");
                    writer.WriteString("all");
                    writer.WriteEndAttribute();

                    // just write the name
                    writer.WriteStartAttribute("name");
                    writer.WriteString(prop.Name);
                    writer.WriteEndAttribute();

                    writer.WriteEndElement(); // property

                    writer.WriteString(Environment.NewLine);
                }

                AddCtor(writer);
                writer.WriteEndElement(); //type
                writer.WriteString(Environment.NewLine);
            }

            writer.WriteEndElement(); // assembly
            writer.WriteString(Environment.NewLine);

        }

        writer.WriteEndElement();
        writer.Flush();
        writer.Close();

    }

    public static Options Options { get; set; }

    private static Assembly? CurrentDomainOnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        foreach (var d in searchDirectories)
        {
            if (TryFindInPath(args.Name, d, out Assembly ass))
            {
                return ass;
            }
        }

        foreach (var refAss in referenceAssemblies)
        {
            if (TryFindInPath(args.Name, Path.GetDirectoryName(refAss), out Assembly ass))
            {
                return ass;
            }
        }

        return null;
    }

    private static bool TryFindInPath(string assemblyName, string directory, out Assembly? assembly)
    {
        var path = Path.Combine(directory, assemblyName.Split(',')[0] + ".dll");
        try
        {
            assembly = Assembly.LoadFile(path);
            return true;
        }
        catch
        {
            assembly = null;
            return false;
        }
    }

    private static void AddCtor(XmlTextWriter writer)
    {
        writer.WriteStartElement("method");
        writer.WriteStartAttribute("name");
        writer.WriteString(".ctor");
        writer.WriteEndAttribute();
        writer.WriteEndElement();
    }

    private static void AddSetterTriggerConditionDependencies(XDocument xml, LoadedAssembly asm, BamlDecompilationResult result)
    {
        foreach (var element in xml.Elements())
        //foreach (var setter in xml.Elements().Where(e => e.Name == "Setter"))
        {
            AddSetterTriggerConditionDependencies(element, asm, result);
        }
    }

    private static void AddSetterTriggerConditionDependencies(XElement element, LoadedAssembly asm, BamlDecompilationResult result)
    {
        if (element.Name.LocalName == "Setter" || element.Name.LocalName == "Condition" || element.Name.LocalName == "Trigger")
        {
            if (element.Name.LocalName == "Condition")
            {

            }

            var property = element.Attributes().FirstOrDefault(a => a.Name == "Property");
            if (property == null)
            {
                // Could be with a binding 
                // <Condition Value="True" Binding="{Binding Path=HasLabel, RelativeSource={RelativeSource AncestorType=telerik:RadWatermarkTextBox}}" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />
                var binding = element.Attributes().FirstOrDefault(a => a.Name == "Binding");
                if (binding != null)
                {
                    Console.Error.WriteLine($"Condition with Binding not supported {element}.  Binding Path will not be rooted");
                    return;
                }

                // Mulitbinding also not supported
                // <Condition Value="True" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                //   <Condition.Binding>
                //     <MultiBinding Mode="OneWay" Converter="{StaticResource MultiBindingBooleanOrConverter}">
                //       <Binding Path="ContentPosition" Mode="OneWay" ConverterParameter="Left" Converter="{StaticResource EnumToBooleanConverter}" RelativeSource="{RelativeSource Self}" />
                //       <Binding Path="ContentPosition" Mode="OneWay" ConverterParameter="Right" Converter="{StaticResource EnumToBooleanConverter}" RelativeSource="{RelativeSource Self}" />
                //     </MultiBinding>
                //   </Condition.Binding>
                // </Condition>

                Console.Error.WriteLine($"Condition with MulitBinding not supported {element}.  Binding Paths will not be rooted");
                return;
            }

            if (property != null && property.Value.Contains('.'))
            {
                AddDependencyPropertyProperty(property.Value, asm, result, element);
                return;
            }

            var typeName = GetTargetTypeName(element);
            if (typeName != null)
            {
                // clr-namespace:Telerik.Windows.Controls.GridView;assembly=Telerik.Windows.Controls.GridView
                var assAndType = GetAssemblyShortNameAndTypeFullName(typeName, asm, result, element);
                if (assAndType != null)
                {
                    AddTrimmerProperty(assAndType.Value.AssemblyShortName, assAndType.Value.TypeFullName, property.Value);
                }
            }
        }

        foreach (var child in element.Elements())
        {
            AddSetterTriggerConditionDependencies(child, asm, result);
        }
    }

    private static string GetTargetTypeName(XElement? element)
    {
        if (element.Name.LocalName == "Trigger")
        {

        }
        var target = GetTargetTypeAttribute(element.Parent);
        if (target != null)
        {
            // e.g. {x:Type tkg:GridViewHeaderCell}
            if (target.Value.StartsWith("{x:Type "))
            {
                var typeParts = target.Value.Split(' ');
                if (typeParts.Length > 1)
                {
                    var targetString = typeParts[1];

                    var typeName = typeParts[1];
                    if (targetString.Contains(":"))
                    {
                        var parts = targetString.Split(':');
                        typeName = parts[1];
                    }

                    return typeName.TrimEnd('}');
                }

                throw new NotImplementedException($"could not get target type from target Value {target.Value}");
            }

            throw new NotImplementedException($"could not get target type, as no x:Type {element}");
        }

        throw new NotImplementedException($"no target for {element}");
    }

    private static XAttribute? GetTargetTypeAttribute(XElement? element)
    {
        if (element == null)
        {
            return null;
        }

        var target = element?.Attributes().FirstOrDefault(a => a.Name == "TargetType");
        if (target != null)
        {
            return target;
        }

        return GetTargetTypeAttribute(element.Parent);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="property">e.g. Control.BorderThickness</param>
    /// <param name="asm"></param>
    /// <param name="result"></param>
    /// <param name="element"></param>
    private static void AddDependencyPropertyProperty(string property, LoadedAssembly asm,
        BamlDecompilationResult result, XElement element)
    {
        var propertyParts = property.Split('.');
        var className = propertyParts[0];
        var propertyName = propertyParts[1];
        if (className == "controls:RadDataForm")
        {

        }

        var loadedAss = GetAssemblyShortNameAndTypeFullName(className, asm, result, element);
        if (loadedAss == null)
        {
            return;
        }

        if (loadedAss.Value.AssemblyShortName == null)
        {
            throw new Exception($"could not find assembly for {property} in {element}");
        }

        // Just add the Set ?
        AddDependencyPropertyMethods(loadedAss.Value.AssemblyShortName, loadedAss.Value.TypeFullName, propertyName);
    }

    private static string GetTypeName(string className)
    {
        //tkc:TextSearch
        if (className.Contains(':'))
        {
            var parts = className.Split(':');
            return parts[1];
        }

        return className;
    }

    private static AssemblyAndType? GetAssemblyShortNameAndTypeFullName(string className, LoadedAssembly asm,
        BamlDecompilationResult result, XElement element)
    {
        if (className.Contains('.'))
        {
            throw new NotImplementedException($"AssemblyName from {className} not implemented");
        }

        //tkc:TextSearch
        if (className.Contains(':'))
        {
            var parts = className.Split(':');
            var ns = parts[0];
            // tkc:TextSearch

            //< ResourceDictionary xmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns: x = "http://schemas.microsoft.com/winfx/2006/xaml"
            //xmlns: tkn = "clr-namespace:Telerik.Windows.Controls;assembly=Telerik.Windows.Controls.Navigation"
            //xmlns: tkc = "clr-namespace:Telerik.Windows.Controls;assembly=Telerik.Windows.Controls"

            // assume standard
            var nsAtt = FindNsAtt(element, ns);
            var nsValue = nsAtt.Value;
            var attValueParts = nsValue.Split(';');
            var assemblyPart = attValueParts.FirstOrDefault(p => p.StartsWith("assembly="));
            var clrNamespace = attValueParts.FirstOrDefault(p => p.StartsWith("clr-namespace:"));
            string? typeNameNs  = null;
            var ret = new AssemblyAndType();
            if (clrNamespace != null)
            {
                typeNameNs = clrNamespace.Split(':')[1];
                if (typeNameNs != null)
                {
                    ret.TypeFullName = typeNameNs + "." + parts[1];
                }
            }

            if (assemblyPart != null)
            {
                var nvParts = assemblyPart.Split('=');
                var ass = asm.AssemblyList.GetAssemblies().FirstOrDefault(a => a.ShortName == nvParts[1]);

                if (ass == null)
                {
                    // look in -r assemblies
                    var ass2 = refAssemblies.FirstOrDefault(a => a.GetName().Name == nvParts[1]);
                    if (ass2 == null)
                    {
                        if (Options.ExcludedAssemblies.Contains(nvParts[1]))
                        {
                            return null;
                        }
                        throw new Exception($"Could not find assembly for {nvParts[1]}.  Try adding it with -r or move it above {asm.FileName}");
                    }

                    ret.AssemblyShortName = ass2.GetName().Name;
                }
                else
                {
                    ret.AssemblyShortName = ass.ShortName;
                }
                return ret;
            }

            // clr-namespace:Telerik.Windows.Controls.MaterialControls
            if (nsValue.StartsWith("clr-namespace:"))
            {
                var typeName = typeNameNs + "." + parts[1];
                if (HasClass(asm, result, typeName, out _))
                {
                    ret.AssemblyShortName = asm.ShortName;
                    return ret;
                }

                foreach (var refAss in refAssemblies)
                {
                    if (refAss.GetType(typeName) != null)
                    {
                        ret.AssemblyShortName = refAss.GetName().Name;
                        ret.TypeFullName = typeName;
                        return ret;
                    }
                }
            }

            // try as XmlnsDefinition
            else if (xmlnsDefinitions.TryGetValue(nsValue, out var defs))
            {
                foreach (var attDef in defs)
                {
                    var typeName = attDef.ClrNamespace + "." + parts[1];
                    foreach (var refAss in refAssemblies)
                    {
                        if (refAss.GetType(typeName) != null)
                        {
                            ret.AssemblyShortName = refAss.GetName().Name;
                            ret.TypeFullName = typeName;
                            return ret;
                        }
                    }
                }
            }

            Console.Error.WriteLine(
                $"Did not find {parts[1]} from namespace {nsValue}, you should probably add the containing dll with -r");
        }

        // attempt a naive lookup in the result xaml types
        var xamlType = result.XamlTypes.FirstOrDefault(t => t.TypeName == className);
        if (xamlType != null)
        {
            return new AssemblyAndType
            {
                AssemblyShortName = AssemblyNameFrom(xamlType.FullAssemblyName),
                TypeFullName = xamlType.ResolvedType?.FullName,
            };
        }

        var pfAss = GetPresentationFrameworkAssembly(asm);
        string? typeFullName = null;
        if (HasClass(pfAss, result, className, out typeFullName))
        {
            return new AssemblyAndType
            {
                AssemblyShortName = pfAss.ShortName,
                TypeFullName = typeFullName,
            };
        }

        var pfCoreAss = GetPresentationCore(asm);
        if (HasClass(pfCoreAss, result, className, out typeFullName))
        {
            return new AssemblyAndType
            {
                AssemblyShortName = pfCoreAss.ShortName,
                TypeFullName = typeFullName,
            };
        }

        Console.Error.WriteLine(
            $"Did not find {className}, you should probably add the containing dll with -r");

        throw new Exception($"Could not resolve type {className}");
    }

    private static XAttribute? FindNsAtt(XElement? element, string ns)
    {
        if (element == null)
        {
            return null;
        }

        var res = element.Attributes().FirstOrDefault(a => a.Name.NamespaceName == "http://www.w3.org/2000/xmlns/"
                                                          && a.Name.LocalName == ns);
        if (res != null)
        {
            return res;
        }

        return FindNsAtt(element.Parent, ns);
    }

    private static bool HasClass(LoadedAssembly ass, BamlDecompilationResult result, string className, out string typeFullName)
    {
        var typeName = GetTypeName(className);

        var t = result.XamlTypes.FirstOrDefault(t => (t.Assembly?.AssemblyName == ass.ShortName &&
                                             t.TypeName == typeName) );
        typeFullName = t?.ResolvedType?.FullName;

        return t != null;
    }

    private static LoadedAssembly? GetPresentationFrameworkAssembly(LoadedAssembly asm)
    {
        return asm.AssemblyList.GetAssemblies().FirstOrDefault(a => a.ShortName == "PresentationFramework");
    }

    private static LoadedAssembly GetPresentationCore(LoadedAssembly asm)
    {
        return asm.AssemblyList.GetAssemblies().First(a => a.ShortName == "PresentationCore");
    }

    private static void  AddTypesAndMethods(Resource resource, LoadedAssembly asm)
    {
        BamlDecompilerTypeSystem typeSystem = new BamlDecompilerTypeSystem(asm.GetMetadataFileOrNull(), asm.GetAssemblyResolver());
        var decompiler = new XamlDecompiler(typeSystem, new BamlDecompilerSettings());
        decompiler.CancellationToken = new CancellationToken();
        Stream s = resource.TryOpenStream();
        foreach (var entry in new ResourcesFile(s).OrderBy(e => e.Key))
        {
            if (entry.Value is MemoryStream ms)
            {
                if (resource.ResourceType == ResourceType.Embedded)
                {
                    if (entry.Key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entry.Key.Contains("Dashboard", StringComparison.InvariantCultureIgnoreCase))
                        {
                        }

                        var result = decompiler.Decompile(ms);
                        AddTrimmerTypes(asm, result);
                        AddTrimmerProperties(result);
                        AddSetterTriggerConditionDependencies(result.Xaml, asm, result);
                    }
                    else if (entry.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSetterTriggerConditionDependencies(XDocument.Load(ms), asm, null);
                    }
                }
            }
        }
    }

    static void AddTrimmerTypes(LoadedAssembly asm, BamlDecompilationResult result)
    {
        foreach (var t in result.XamlTypes)
        {
            var trimmerType = new TrimmerType
            {
                Assembly = AssemblyNameFrom(t.FullAssemblyName),
                TypeFullName = GetTypeFullNameFromResolvedType(t.ResolvedType)
            };

            if (!trimmerTypes.Contains(trimmerType))
            {
                trimmerTypes.Add(trimmerType);
            }
        }
    }

    static void AddTrimmerProperties(BamlDecompilationResult result)
    {
        foreach (var p in result.XamlProperties)
        {
            if (p.ResolvedMember == null)
            {
                p.TryResolve();
            }

            if (p.ResolvedMember == null)
            {
                var propertyOwnerType = GetTypeFromReferenceAssemblies(p.DeclaringType);
                if (propertyOwnerType == null)
                {
                    Console.WriteLine(
                        $"Could not resolve {p.DeclaringType.TypeName}.{p.PropertyName} from reference assemblies, you could try adding the containing assembly as a direct reference," +
                        $"treating as a dependency property and clr property, if it really is an event, then this will fail to add the correct dependencies.");

                    AddDependencyPropertyMethods(AssemblyNameFrom(p.DeclaringType.FullAssemblyName),
                        p.DeclaringType.ResolvedType.FullName, p.PropertyName);

                    AddTrimmerProperty(AssemblyNameFrom(p.DeclaringType.FullAssemblyName),
                        p.DeclaringType.ResolvedType.FullName, p.PropertyName);
                }
                else
                {
                    // look for "new" properties first
                    var propertyInfo = (MemberInfo)propertyOwnerType.GetProperty(p.PropertyName, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
                                        ?? propertyOwnerType.GetProperty(p.PropertyName) ?? (MemberInfo)propertyOwnerType.GetField(p.PropertyName);
                    if (IsDependencyProperty(propertyOwnerType, p.PropertyName))
                    {
                        AddDependencyPropertyMethods(AssemblyNameFrom(p.DeclaringType.FullAssemblyName), p.DeclaringType.ResolvedType.FullName, p.PropertyName);
                        if (propertyInfo != null)
                        {
                            AddTrimmerProperty(propertyInfo.DeclaringType.Assembly.FullName, propertyInfo.DeclaringType.FullName, p.PropertyName);
                        }
                    }
                    else if (propertyInfo != null)
                    {
                        AddTrimmerProperty(propertyInfo.DeclaringType.Assembly.FullName, propertyInfo.DeclaringType.FullName, p.PropertyName);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Could not add property {p.PropertyName}");
                    }
                }
            }
            else
            {
                if (p.ResolvedMember is IField field)
                {
                    if (field.Type.FullName == "System.Windows.DependencyProperty")
                    {
                        var propIx = p.ResolvedMember.Name.LastIndexOf("Property");
                        var baseName = p.ResolvedMember.Name.Substring(0, propIx);
                        AddDependencyPropertyMethods(AssemblyNameFrom(p.DeclaringType.FullAssemblyName), p.DeclaringType.ResolvedType.FullName, baseName);
                        continue;
                    }
                }

                if (p.ResolvedMember is IEvent ev)
                {
                    if (ev.CanAdd)
                    {
                        AddMethod(AssemblyNameFrom(p.DeclaringType.FullAssemblyName),
                            p.DeclaringType.ResolvedType.FullName,
                            ev.AddAccessor);
                    }

                    if (ev.CanRemove)
                    {
                        AddMethod(AssemblyNameFrom(p.DeclaringType.FullAssemblyName),
                            p.DeclaringType.ResolvedType.FullName,
                            ev.RemoveAccessor);
                    }

                    continue;
                }

                AddTrimmerProperty(p.DeclaringType.FullAssemblyName, GetTypeFullNameFromResolvedType(p.DeclaringType.ResolvedType), p.ResolvedMember.Name);
            }
        }
    }


    // A guess
    private static bool IsDependencyProperty(Type ownerType, string baseName)
    {
        var staticProp = ownerType.GetField(baseName + "Property", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return staticProp != null && staticProp.FieldType.FullName == "System.Windows.DependencyProperty";
    }

    private static void AddTrimmerProperty(string fullAssemblyName, string typeFullName, string propertyName)
    {
        var trimmerProperty = new TrimmerProperty
        {
            Assembly = AssemblyNameFrom(fullAssemblyName),
            TypeFullName = typeFullName,
            Name = propertyName
        };
        if (typeFullName == "Telerik.Windows.Controls.Input.RadComboBox")
        {

        }

        if (!trimmerProperties.Contains(trimmerProperty))
        {
            trimmerProperties.Add(trimmerProperty);
        }
    }

    private static Type? GetTypeFromReferenceAssemblies(XamlType pDeclaringType)
    {
        foreach (var refAss in refAssemblies)
        {
            if (refAss.FullName == pDeclaringType.FullAssemblyName)
            {
                return refAss.GetType(GetTypeFullNameFromResolvedType(pDeclaringType.ResolvedType));
            }
        }

        return null;
    }

    private static string GetTypeFullNameFromResolvedType(IType resolvedType)
    {
        if (resolvedType.TypeParameterCount > 0)
        {
            return resolvedType.ReflectionName.Split('[')[0];
        }
        return resolvedType.FullName;
    }

    private static void AddMethod(string assembly, string typeFullName, IMethod method)
    {
        var trimmerMethod = new TrimmerMethod
        {
            Assembly = assembly,
            TypeFullName = typeFullName,
            Name = method.ReflectionName
        };

        if (typeFullName == "Telerik.Windows.Controls.Input.RadComboBox")
        {

        }

        if (!trimmerMethods.Contains(trimmerMethod))
        {
            trimmerMethods.Add(trimmerMethod);
        }
    }

    private static void AddDependencyPropertyMethods(string assembly, string typeFullName, string baseName)
    {
        AddDependencyPropertyMethod(assembly, typeFullName, "Get", baseName);
        AddDependencyPropertyMethod(assembly, typeFullName, "Set", baseName);
    }

    private static void AddDependencyPropertyMethod(string assembly, string typeFullName, string prefix, string baseName)
    {
        var trimmerMethod = new TrimmerMethod
        {
            Assembly = assembly,
            TypeFullName = typeFullName,
            Name = prefix + baseName
        };
        if (typeFullName == "Telerik.Windows.Controls.Input.RadComboBox")
        {

        }

        if (!trimmerMethods.Contains(trimmerMethod))
        {
            trimmerMethods.Add(trimmerMethod);
        }
    }

    private static string AssemblyNameFrom(string fullAssemblyName)
    {
        var parts = fullAssemblyName.Split(",");
        if (parts.Length < 1) throw new Exception($"could not get assembly name from {fullAssemblyName}");

        return parts[0];
    }
}

