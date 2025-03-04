﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using MultiFunPlayer.Common;
using NLog;
using Stylet;
using StyletIoC;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;

namespace MultiFunPlayer.Plugin;

internal sealed class PluginCompilationResult : IDisposable
{
    public Exception Exception { get; private set; }
    public PluginSettingsBase Settings { get; private set; }
    public UIElement SettingsView { get; private set; }

    private Func<PluginBase> PluginFactory { get; set; }
    private PluginAssemblyLoadContext Context { get; set; }

    public bool Success => Exception == null;

    private PluginCompilationResult() { }

    public static PluginCompilationResult FromFailure(PluginAssemblyLoadContext context, Exception e)
    {
#pragma warning disable IDE0059 // Unnecessary assignment of a value
        context?.Dispose();
        context = null;
#pragma warning restore IDE0059 // Unnecessary assignment of a value

        return new() { Exception = e };
    }

    public static PluginCompilationResult FromSuccess(PluginAssemblyLoadContext context, Func<PluginBase> pluginFactory)
        => new() { Context = context, PluginFactory = pluginFactory };
    public static PluginCompilationResult FromSuccess(PluginAssemblyLoadContext context, Func<PluginBase> pluginFactory, PluginSettingsBase settings, UIElement settingsView)
        => new() { Context = context, PluginFactory = pluginFactory, Settings = settings, SettingsView = settingsView };

    public PluginBase CreatePluginInstance() => PluginFactory?.Invoke();

    private void Dispose(bool disposing)
    {
        PluginFactory = null;
        SettingsView = null;
        Settings = null;

        Context?.Dispose();
        Context = null;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

internal static class PluginCompiler
{
    private readonly static IReadOnlyCollection<string> ValidPluginBaseClasses = [nameof(SyncPluginBase), nameof(AsyncPluginBase)];

    private static Channel<Action> _compileQueue;
    private static Task _compileTask;

    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private static Regex ReferenceRegex { get; } = new Regex(@"^//#r\s+""(?<value>.+?)""\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static IContainer Container { get; set; }
    private static IViewManager ViewManager { get; set; }

    private static IReadOnlyCollection<MetadataReference> _referenceCache;
    private static IReadOnlyCollection<MetadataReference> ReferenceCache
    {
        get
        {
            _referenceCache ??= [.. AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => !string.IsNullOrWhiteSpace(a.Location) ? AssemblyMetadata.CreateFromFile(a.Location).GetReference(filePath: a.Location) : null)
                .NotNull()];

            return _referenceCache;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void QueueCompile(FileInfo pluginFile, Action<PluginCompilationResult> callback)
    {
        _compileQueue ??= Channel.CreateUnbounded<Action>(new UnboundedChannelOptions()
        {
            SingleReader = true,
            SingleWriter = false
        });

        _compileTask ??= Task.Run(DoCompile);
        _compileQueue.Writer.TryWrite(() =>
        {
            Logger.Debug("Compiling plugin [File: {0}]", pluginFile.FullName);
            var result = Compile(pluginFile);
            callback(result);
        });

        static async Task DoCompile()
        {
            await foreach (var compileAction in _compileQueue.Reader.ReadAllAsync())
                compileAction();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static PluginCompilationResult Compile(FileInfo pluginFile)
    {
        var result = InternalCompile(pluginFile);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Default, blocking: false);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginCompilationResult InternalCompile(FileInfo pluginFile)
    {
        var context = new PluginAssemblyLoadContext();
        try
        {
            var references = ReferenceCache.ToList();

            var pluginSource = File.ReadAllText(pluginFile.FullName);
            LoadPluginReferences(pluginFile, pluginSource, context, references);

            var sourcePath = pluginFile.FullName;
            var pdbPath = Path.ChangeExtension(sourcePath, ".pdb");

            var sourceBuffer = Encoding.UTF8.GetBytes(pluginSource);
            var sourceText = SourceText.From(
                sourceBuffer,
                sourceBuffer.Length,
                Encoding.UTF8,
                canBeEmbedded: true
            );

            var syntaxTree = CSharpSyntaxTree.ParseText(
                sourceText,
                path: sourcePath
            );

            var pluginClasses = syntaxTree.GetRoot()
                                          .DescendantNodes()
                                          .OfType<ClassDeclarationSyntax>()
                                          .Where(s => s.BaseList.Types.Any(x => ValidPluginBaseClasses.Contains(x.ToString())))
                                          .ToList();

            if (pluginClasses.Count == 0)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("Unable to find base Plugin class"));
            if (pluginClasses.Count > 1)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("Found more than one base Plugin class"));

            var assemblyName = $"Plugin_{Path.GetFileNameWithoutExtension(pluginFile.Name)}";
            var encoded = CSharpSyntaxTree.Create(
                syntaxTree.GetRoot() as CSharpSyntaxNode,
                null,
                sourcePath,
                Encoding.UTF8
            );

            var compilationOptions = new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: Debugger.IsAttached ? OptimizationLevel.Debug : OptimizationLevel.Release,
                warningLevel: 4,
                deterministic: true
            );

            var compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: [encoded],
                references: references,
                options: compilationOptions
            );

            var emitOptions = new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: pdbPath
            );

            var embeddedTexts = new List<EmbeddedText>
            {
                EmbeddedText.FromSource(sourcePath, sourceText)
            };

            using var peStream = new MemoryStream();
            using var pdbStream = new MemoryStream();

            var emitResult = compilation.Emit(
                peStream: peStream,
                pdbStream: pdbStream,
                options: emitOptions,
                embeddedTexts: embeddedTexts
            );

            if (!emitResult.Success)
            {
                var diagnostics = emitResult.Diagnostics.Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error);
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("Plugin failed to compile due to errors", diagnostics));
            }

            peStream.Seek(0, SeekOrigin.Begin);
            pdbStream.Seek(0, SeekOrigin.Begin);

            var assembly = context.LoadFromStream(peStream, pdbStream);
            var pluginType = Array.Find(assembly.GetExportedTypes(), t => t.IsAssignableTo(typeof(PluginBase)));

            if (pluginType == null)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("Unable to find exported Plugin type"));

            var pluginConstructors = pluginType.GetConstructors();
            if (pluginConstructors.Length == 0)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("No public plugin constructor found"));

            if (pluginConstructors.Length != 1)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("Plugin can only have one constructor"));

            var constructorParameters = pluginConstructors[0].GetParameters();
            if (constructorParameters.Length > 1)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException("Plugin constructor can only have zero or one parameters"));

            var settingsType = constructorParameters.FirstOrDefault()?.ParameterType;
            if (settingsType?.IsAssignableTo(typeof(PluginSettingsBase)) == false)
                return PluginCompilationResult.FromFailure(context, new PluginCompileException($"Plugin constructor parameter must extend \"{nameof(PluginSettingsBase)}\""));

            if (settingsType == null)
            {
                return PluginCompilationResult.FromSuccess(context, CreatePluginInstance);

                PluginBase CreatePluginInstance()
                {
                    var instance = Activator.CreateInstance(pluginType) as PluginBase;
                    Container.BuildUp(instance);
                    return instance;
                }
            }
            else
            {
                var settings = (PluginSettingsBase)Activator.CreateInstance(settingsType);

                var settingsView = default(UIElement);
                Execute.OnUIThreadSync(() =>
                {
                    settingsView = settings.CreateView();

                    if (settingsView != null)
                        ViewManager.BindViewToModel(settingsView, settings);
                });

                return PluginCompilationResult.FromSuccess(context, CreatePluginInstance, settings, settingsView);

                PluginBase CreatePluginInstance()
                {
                    var instance = Activator.CreateInstance(pluginType, [settings]) as PluginBase;
                    Container.BuildUp(instance);
                    return instance;
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "Plugin compiler failed with exception");
            return PluginCompilationResult.FromFailure(context, new PluginCompileException("Plugin compiler failed with exception", e));
        }
    }

    private static void LoadPluginReferences(FileInfo pluginFile, string pluginSource, PluginAssemblyLoadContext context, List<MetadataReference> references)
    {
        foreach (var match in ReferenceRegex.Matches(pluginSource).NotNull().Where(m => m.Success))
        {
            var reference = match.Groups["value"].Value;
            var added = TryAddByName(reference)
                     || TryAddByPath(Path.Join(pluginFile.DirectoryName, reference))
                     || TryAddByPath(reference);

            if (!added)
                Logger.Warn("Failed to load assembly \"{0}\" for plugin \"{1}\"", reference, pluginFile.Name);
        }

        bool TryAddByPath(string path) => File.Exists(path) && TryAddReference(() => context.LoadAndGetReferenceFromAssemblyPath(path));
        bool TryAddByName(string assemblyName) => TryAddReference(() => context.LoadAndGetReferenceFromAssemblyName(new AssemblyName(assemblyName)));

        bool TryAddReference(Func<MetadataReference> referenceFactory)
        {
            try { references.Add(referenceFactory()); }
            catch { return false; }

            return true;
        }
    }

    public static void Initialize(IContainer container)
    {
        Container = container;
        ViewManager = container.Get<IViewManager>();
    }
}

internal sealed class PluginAssemblyLoadContext() : AssemblyLoadContext(isCollectible: true), IDisposable
{
    private readonly List<AssemblyMetadata> _assemblyMetadata = [];

    protected override Assembly Load(AssemblyName assemblyName) => null;

    public MetadataReference LoadAndGetReferenceFromAssemblyPath(string path) => GetReference(LoadFromAssemblyPath(path));
    public MetadataReference LoadAndGetReferenceFromAssemblyName(AssemblyName assemblyName) => GetReference(LoadFromAssemblyName(assemblyName));

    private PortableExecutableReference GetReference(Assembly assembly)
    {
        var assemblyMetadata = AssemblyMetadata.CreateFromFile(assembly.Location);
        _assemblyMetadata.Add(assemblyMetadata);
        return assemblyMetadata.GetReference(filePath: assembly.Location);
    }

    private void Dispose(bool disposing)
    {
        foreach (var metadata in _assemblyMetadata)
            metadata.Dispose();

        _assemblyMetadata.Clear();
        Unload();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

internal sealed class PluginCompileException : Exception
{
    public PluginCompileException() { }

    public PluginCompileException(string message)
        : base(message) { }

    public PluginCompileException(string message, Exception innerException)
        : base(message, innerException) { }

    public PluginCompileException(string message, IEnumerable<Diagnostic> diagnostics)
        : this($"{message}\n{string.Join('\n', diagnostics.Select(d => d.ToString()))}") { }
}
