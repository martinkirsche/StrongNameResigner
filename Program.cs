using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Options;

namespace StrongNameResigner
{
    class Program
    {
        static int verbosity;

        public static string ExecutableFileName
        {
            get
            {
                return System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".exe";
            }
        }

        static BaseAssemblyResolver assemblyResolver = new DefaultAssemblyResolver();

        public static void Main(string[] args)
        {
            bool showHelp = false;
            bool listDependenciesOnly = false;

            foreach (var s in assemblyResolver.GetSearchDirectories())
            {
                assemblyResolver.RemoveSearchDirectory(s);
            }

            var p = new OptionSet()
            {
                { "a|assembly-search-directory=", "add a search directory into the assembly resolver", v => assemblyResolver.AddSearchDirectory(v) },
                { "d|list-dependencies-only", "output a list of detected dependencies and exit", v => listDependenciesOnly = v != null },
                { "k|key-pair-file=", "resign assembly with the key pair in file (.snk)", v => LoadKeyPairFile(v) },
                { "v", "increase verbosity", v => { if (v != null) ++verbosity; } },
                { "h|help",  "show this message and exit", v => showHelp = v != null },
            };

            if (0 == args.Length)
            {
                TryHelp();
                return;
            }

            List<string> assemblies;
            try
            {
                assemblies = p.Parse(args);
            }
            catch (OptionException e)
            {
                Error(e.Message);
                TryHelp();
                return;
            }

            if (showHelp)
            {
                ShowHelp(p);
                return;
            }

            if (0 == assemblies.Count)
            {
                Error("No assembly names were given.");
                TryHelp();
                return;
            }

            if (0 == assemblyResolver.GetSearchDirectories().Length)
            {
                assemblyResolver.AddSearchDirectory(".");
            }

            IEnumerable<AssemblyDefinition> dependendAssemblies;
            try
            {
                dependendAssemblies = FindDependendAssemblies(assemblies).ToList();
            }
            catch (Exception exception)
            {
                Error("Unable find dependend assemblies: {0}", exception.ToString());
                return;
            }

            if (listDependenciesOnly)
            {
                foreach (var a in dependendAssemblies)
                {
                    Console.WriteLine(a.MainModule.FullyQualifiedName);
                }
                return;
            }

            foreach (var d in ProcessAssemblies(dependendAssemblies).ToList())
            {
                try
                {
                    WriteAssembly(d);
                }
                catch (Exception exception)
                {
                    Error("Unable to write assembly: {0}", exception.ToString());
                    continue;
                }
            }
        }

        static void TryHelp()
        {
            Console.WriteLine(string.Format("Try »{0} --help« for more information.", ExecutableFileName));
        }

        static StrongNameKeyPair strongNameKeyPair = null;
        static byte[] publicKeyToken = new byte[0];

        static void LoadKeyPairFile(string fileName)
        {
            strongNameKeyPair =
                new StrongNameKeyPair(File.ReadAllBytes(fileName));
            publicKeyToken = new SHA1Managed().ComputeHash(strongNameKeyPair.PublicKey)
                .Reverse().Take(8).ToArray();
        }

        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: {0} [Option]... AssemblyName...", ExecutableFileName);
            Console.WriteLine("Resign a list of assemblies and its dependencies.");
            Console.WriteLine("If no key pair is specified, the strong name signature will be removed.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        static void Error(string format, params object[] args)
        {
            Console.Error.WriteLine(format, args);
        }

        static void Debug(string format, params object[] args)
        {
            if (verbosity > 1)
            {
                Console.WriteLine(format, args);
            }
        }

        static void Info(string format, params object[] args)
        {
            if (verbosity > 0)
            {
                Console.WriteLine(format, args);
            }
        }

        static IEnumerable<AssemblyDefinition> ProcessAssemblies(IEnumerable<AssemblyDefinition> assemblies)
        {
            foreach (var d in assemblies)
            {
                try
                {
                    Info("Processing assembly {0}", d.FullName);
                    FixAssemblyReferences(d, assemblies);
                    FixInternalsVisibleToAttribute(d, assemblies);
                    FixCustomAttributesReferences(d, assemblies);
                    FixResourceReferences(d, assemblies);
                }
                catch (Exception exception)
                {
                    Error("Assembly will be skipped: {0}", exception.ToString());
                    continue;
                }
                yield return d;
            }
        }

        static AssemblyDefinition LoadAssemblyDefinition(string fileName)
        {
            Debug("Loading {0}", fileName);
            return AssemblyDefinition.ReadAssembly(fileName,
                new ReaderParameters { AssemblyResolver = assemblyResolver });
        }

        static IEnumerable<AssemblyDefinition> FindDependendAssemblies(List<string> assemblyFullNames)
        {
            List<string> dependencies = new List<string>();

            foreach (var fullName in assemblyFullNames)
            {
                Debug("Loading {0} for dependency searching.", fullName);
                var assemblyDefinition = assemblyResolver.Resolve(fullName);
                var fileName = assemblyDefinition.MainModule.FullyQualifiedName;
                dependencies.Add(assemblyDefinition.FullName);
                yield return assemblyDefinition;
            }

            bool retry = false;
            do
            {
                retry = false;
                foreach (var searchDirectory in assemblyResolver.GetSearchDirectories())
                {
                    if (!Directory.Exists(searchDirectory)) { continue; }
                    foreach (var fileName in Directory.GetFiles(searchDirectory))
                    {
                        if ((!fileName.ToLower().EndsWith(".dll") &&
                            !fileName.ToLower().EndsWith(".exe"))) { continue; }
                        AssemblyDefinition assemblyDefinition = null;
                        try
                        {
                            assemblyDefinition = LoadAssemblyDefinition(fileName);
                        }
                        catch (BadImageFormatException exception)
                        {
                            Debug("Unable to load {0}: {1}", fileName, exception.Message);
                            continue;
                        }
                        catch (Exception exception)
                        {
                            Error("Unknown error while loading {0}: {1}", fileName, exception.ToString());
                            continue;
                        }
                        if (dependencies.Contains(assemblyDefinition.FullName)) { continue; }
                        foreach (var r in assemblyDefinition.MainModule.AssemblyReferences)
                        {
                            if (!dependencies.Contains(r.FullName)) { continue; }
                            retry = true;
                            dependencies.Add(assemblyDefinition.FullName);
                            yield return assemblyDefinition;
                            break;
                        }
                    }
                }
            } while (retry);
        }

        static void FixResourceReferences(AssemblyDefinition assemblyDefinition, IEnumerable<AssemblyDefinition> dependencies)
        {
            Info("Fixing resource references.");
            var searchAndReplace = new List<Tuple<byte[], byte[]>>();
            foreach (var d in dependencies)
            {
                string newName =
                    new AssemblyNameDefinition(d.Name.Name, d.Name.Version)
                    {
                        PublicKeyToken = publicKeyToken
                    }.FullName;
                if (0 == publicKeyToken.Length) { newName += new string(' ', (8 * 2) - "null".Length); }
                string oldName = d.FullName;
                if (0 == d.Name.PublicKeyToken.Length) { oldName += new string(' ', (8 * 2) - "null".Length); }
                if (newName.Length != oldName.Length || newName == oldName) { continue; }
                searchAndReplace.Add(
                    Tuple.Create(Encoding.UTF8.GetBytes(oldName), Encoding.UTF8.GetBytes(newName)));
            }
            if (0 == searchAndReplace.Count)
            {
                return;
            }

            foreach (var resource in assemblyDefinition.MainModule.Resources.ToList())
            {
                var embeddedResource = resource as EmbeddedResource;
                if (embeddedResource == null ||
                    !embeddedResource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                using (var resourceOutput = new MemoryStream())
                {
                    using (var resourceInput = embeddedResource.GetResourceStream())
                    {
                        BinaryUtility.Replace(new BinaryReader(resourceInput), new BinaryWriter(resourceOutput), searchAndReplace);
                        assemblyDefinition.MainModule.Resources.Remove(resource);
                        assemblyDefinition.MainModule.Resources.Add(new EmbeddedResource(embeddedResource.Name, resource.Attributes, resourceOutput.ToArray()));                        
                    }
                }
            }
        }


        static void FixCustomAttributesReferences(AssemblyDefinition assemblyDefinition, IEnumerable<AssemblyDefinition> dependencies)
        {
            Info("Fixing type references in custom attributes.");
            foreach (var t in assemblyDefinition.MainModule.Types)
            {
                FixCustomAttributesReferences(t, dependencies);
            }
        }

        static void FixCustomAttributesReferences(TypeDefinition typeDefinition, IEnumerable<AssemblyDefinition> dependencies)
        {                
            foreach(var n in typeDefinition.NestedTypes)
            {
                FixCustomAttributesReferences(n, dependencies);
            }
            foreach (var attribute in typeDefinition.CustomAttributes)
            {
                foreach (var argument in attribute.ConstructorArguments)
                {
                    FixCustomAttributesReferences(argument, dependencies);
                }
                foreach (var property in attribute.Properties)
                {
                    FixCustomAttributesReferences(property.Argument, dependencies);
                }
            }
        }

        static void FixCustomAttributesReferences(CustomAttributeArgument argument, IEnumerable<AssemblyDefinition> dependencies)
        {
            {
                AssemblyDefinition existingDependency;
                AssemblyNameReference assemblyNameReference;
                if (null != (assemblyNameReference = argument.Type.Scope as AssemblyNameReference) &&
                    null != (existingDependency = dependencies.SingleOrDefault(x => x.FullName == assemblyNameReference.FullName)))
                {
                    assemblyNameReference.PublicKeyToken = publicKeyToken;
                    return;
                }
            }
            {
                if (argument.Type.FullName != "System.Type") { return; }
                if (!(argument.Value is MemberReference)) { return; }
                object scope = ((dynamic)argument.Value).Scope;
                if (!(scope is AssemblyNameReference)) { return; }
                AssemblyNameReference assemblyNameReference = (AssemblyNameReference)scope;
                if (!dependencies.Any(x => x.FullName == assemblyNameReference.FullName)) { return; }
                assemblyNameReference.PublicKeyToken = publicKeyToken;
            }
        }


        static void FixInternalsVisibleToAttribute(AssemblyDefinition assemblyDefinition, IEnumerable<AssemblyDefinition> dependencies)
        {
            Info("Fixing InternalsVisibleToAttribute references.");
            foreach (var attribute in assemblyDefinition.CustomAttributes)
            {
                if (attribute.AttributeType.Name != "InternalsVisibleToAttribute") { continue; }
                var argument = attribute.ConstructorArguments.First();
                var v = argument.Value.ToString().Split(new string[] { "," }, StringSplitOptions.None);
                if (2 != v.Length) { continue; }
                var name = v[0].Trim();
                if (!dependencies.Any(x => x.Name.Name == name)) { continue; }
                attribute.ConstructorArguments.Remove(argument);
                attribute.ConstructorArguments.Insert(0,
                    new CustomAttributeArgument(argument.Type, string.Format("{0}, PublicKey={1}", name,
                        BitConverter.ToString(strongNameKeyPair.PublicKey).Replace("-", ""))));
            }
        }

        static void FixAssemblyReferences(AssemblyDefinition assemblyDefinition, IEnumerable<AssemblyDefinition> dependencies)
        {
            Info("Fixing assembly references.");
            foreach (var r in assemblyDefinition.MainModule.AssemblyReferences)
            {
                if (!dependencies.Any(x => x.FullName == r.FullName)) { continue; }
                Debug("Fixing assembly reference to {0}.", r.FullName);
                r.PublicKeyToken = publicKeyToken;
            }
        }

        static void WriteAssembly(AssemblyDefinition assemblyDefinition)
        {
            Info("Writing assembly to {0}.", assemblyDefinition.MainModule.FullyQualifiedName);
            if (0 == publicKeyToken.Length)
            {
                assemblyDefinition.Name.HasPublicKey = false;
                assemblyDefinition.Name.PublicKey = new byte[0];
                assemblyDefinition.MainModule.Attributes &= ~ModuleAttributes.StrongNameSigned;
            }
            assemblyDefinition.Write(assemblyDefinition.MainModule.FullyQualifiedName,
                new WriterParameters() { StrongNameKeyPair = strongNameKeyPair });
        }
    }
}
