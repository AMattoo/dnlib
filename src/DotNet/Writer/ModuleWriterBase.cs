﻿using System.Collections.Generic;
using System.IO;
using dot10.IO;
using dot10.PE;
using dot10.W32Resources;

namespace dot10.DotNet.Writer {
	/// <summary>
	/// Common module writer options base class
	/// </summary>
	public class ModuleWriterOptionsBase {
		IModuleWriterListener listener;
		MetaDataOptions metaDataOptions;
		ILogger logger;
		ILogger metaDataLogger;
		Win32Resources win32Resources;

		/// <summary>
		/// Gets/sets the listener
		/// </summary>
		public IModuleWriterListener Listener {
			get { return listener; }
			set { listener = value; }
		}

		/// <summary>
		/// Gets/sets the logger. If this is <c>null</c>, any errors result in a
		/// <see cref="ModuleWriterException"/> being thrown. To disable this behavior, either
		/// create your own logger or use <see cref="DummyLogger.NoThrowInstance"/>.
		/// </summary>
		public ILogger Logger {
			get { return logger; }
			set { logger = value; }
		}

		/// <summary>
		/// Gets/sets the <see cref="MetaData"/> writer logger. If this is <c>null</c>, use
		/// <see cref="Logger"/>.
		/// </summary>
		public ILogger MetaDataLogger {
			get { return metaDataLogger; }
			set { metaDataLogger = value; }
		}

		/// <summary>
		/// Gets/sets the <see cref="MetaData"/> options. This is never <c>null</c>.
		/// </summary>
		public MetaDataOptions MetaDataOptions {
			get { return metaDataOptions ?? (metaDataOptions = new MetaDataOptions()); }
			set { metaDataOptions = value; }
		}

		/// <summary>
		/// Gets/sets the Win32 resources. If this is <c>null</c>, use the module's
		/// Win32 resources if any.
		/// </summary>
		public Win32Resources Win32Resources {
			get { return win32Resources; }
			set { win32Resources = value; }
		}

		/// <summary>
		/// <c>true</c> if method bodies can be shared (two or more method bodies can share the
		/// same RVA), <c>false</c> if method bodies can't be shared. Don't enable it if there
		/// must be a 1:1 relationship with method bodies and their RVAs.
		/// </summary>
		public bool ShareMethodBodies { get; set; }

		/// <summary>
		/// <c>true</c> if the PE header CheckSum field should be updated, <c>false</c> if the
		/// CheckSum field isn't updated.
		/// </summary>
		public bool AddCheckSum { get; set; }

		/// <summary>
		/// Default constructor
		/// </summary>
		protected ModuleWriterOptionsBase() {
			ShareMethodBodies = true;
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="module">The module</param>
		protected ModuleWriterOptionsBase(ModuleDef module)
			: this(module, null) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="module">The module</param>
		/// <param name="listener">Module writer listener</param>
		protected ModuleWriterOptionsBase(ModuleDef module, IModuleWriterListener listener) {
			this.listener = listener;
			this.ShareMethodBodies = true;
			this.MetaDataOptions.MetaDataHeaderOptions.VersionString = module.RuntimeVersion;

			// Some tools crash if #GUID is missing so always create it by default
			this.MetaDataOptions.Flags |= MetaDataFlags.AlwaysCreateGuidHeap;
		}
	}

	/// <summary>
	/// Module writer base class
	/// </summary>
	public abstract class ModuleWriterBase : IMetaDataListener, ILogger {
		/// <summary>Default alignment of all constants</summary>
		protected internal const uint DEFAULT_CONSTANTS_ALIGNMENT = 8;
		/// <summary>Default alignment of all method bodies</summary>
		protected const uint DEFAULT_METHODBODIES_ALIGNMENT = 4;
		/// <summary>Default alignment of all .NET resources</summary>
		protected const uint DEFAULT_NETRESOURCES_ALIGNMENT = 8;
		/// <summary>Default alignment of the .NET metadata</summary>
		protected const uint DEFAULT_METADATA_ALIGNMENT = 4;
		/// <summary>Default Win32 resources alignment</summary>
		protected internal const uint DEFAULT_WIN32_RESOURCES_ALIGNMENT = 8;

		/// <summary>See <see cref="DestinationStream"/></summary>
		protected Stream destStream;
		/// <summary>See <see cref="Constants"/></summary>
		protected UniqueChunkList<ByteArrayChunk> constants;
		/// <summary>See <see cref="MethodBodies"/></summary>
		protected MethodBodyChunks methodBodies;
		/// <summary>See <see cref="NetResources"/></summary>
		protected NetResources netResources;
		/// <summary>See <see cref="MetaData"/></summary>
		protected MetaData metaData;
		/// <summary>See <see cref="Win32Resources"/></summary>
		protected Win32ResourcesChunk win32Resources;
		/// <summary>Offset where the module is written. Usually 0.</summary>
		protected long destStreamBaseOffset;
		IModuleWriterListener listener;

		/// <summary>
		/// Returns the module writer options
		/// </summary>
		protected abstract ModuleWriterOptionsBase TheOptions { get; }

		/// <summary>
		/// Gets/sets the module writer listener
		/// </summary>
		protected IModuleWriterListener Listener {
			get { return listener ?? DummyModuleWriterListener.Instance; }
			set { listener = value; }
		}

		/// <summary>
		/// Gets the destination stream
		/// </summary>
		public Stream DestinationStream {
			get { return destStream; }
		}

		/// <summary>
		/// Gets the constants
		/// </summary>
		public UniqueChunkList<ByteArrayChunk> Constants {
			get { return constants; }
		}

		/// <summary>
		/// Gets the method bodies
		/// </summary>
		public MethodBodyChunks MethodBodies {
			get { return methodBodies; }
		}

		/// <summary>
		/// Gets the .NET resources
		/// </summary>
		public NetResources NetResources {
			get { return netResources; }
		}

		/// <summary>
		/// Gets the .NET metadata
		/// </summary>
		public MetaData MetaData {
			get { return metaData; }
		}

		/// <summary>
		/// Gets the Win32 resources or <c>null</c> if there's none
		/// </summary>
		public Win32ResourcesChunk Win32Resources {
			get { return win32Resources; }
		}

		/// <summary>
		/// Writes the module to a file
		/// </summary>
		/// <param name="fileName">File name. The file will be truncated if it exists.</param>
		public void Write(string fileName) {
			using (var dest = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) {
				dest.SetLength(0);
				try {
					Write(dest);
				}
				catch {
					// Writing failed. Delete the file since it's useless.
					dest.Close();
					DeleteFileNoThrow(fileName);
					throw;
				}
			}
		}

		static void DeleteFileNoThrow(string fileName) {
			try {
				File.Delete(fileName);
			}
			catch {
			}
		}

		/// <summary>
		/// Writes the module to a <see cref="Stream"/>
		/// </summary>
		/// <param name="dest">Destination stream</param>
		public void Write(Stream dest) {
			Listener = TheOptions.Listener ?? DummyModuleWriterListener.Instance;
			destStream = dest;
			destStreamBaseOffset = destStream.Position;
			Listener.OnWriterEvent(this, ModuleWriterEvent.Begin);
			WriteImpl();
			Listener.OnWriterEvent(this, ModuleWriterEvent.End);
		}

		/// <summary>
		/// Writes the module to <see cref="destStream"/>. <see cref="Listener"/> and
		/// <see cref="destStream"/> have been initialized when this method is called.
		/// </summary>
		protected abstract void WriteImpl();

		/// <summary>
		/// Creates the .NET metadata chunks (constants, method bodies, .NET resources,
		/// the metadata, and Win32 resources)
		/// </summary>
		/// <param name="module"></param>
		protected void CreateMetaDataChunks(ModuleDef module) {
			constants = new UniqueChunkList<ByteArrayChunk>();
			methodBodies = new MethodBodyChunks(TheOptions.ShareMethodBodies);
			netResources = new NetResources(DEFAULT_NETRESOURCES_ALIGNMENT);

			metaData = MetaData.Create(module, constants, methodBodies, netResources, TheOptions.MetaDataOptions);
			metaData.Logger = TheOptions.MetaDataLogger ?? this;
			metaData.Listener = this;

			var w32Resources = GetWin32Resources();
			if (w32Resources != null)
				win32Resources = new Win32ResourcesChunk(w32Resources);
		}

		/// <summary>
		/// Gets the Win32 resources that should be written to the new image or <c>null</c> if none
		/// </summary>
		protected abstract Win32Resources GetWin32Resources();

		/// <summary>
		/// Calculates <see cref="RVA"/> and <see cref="FileOffset"/> of all <see cref="IChunk"/>s
		/// </summary>
		/// <param name="chunks">All chunks</param>
		/// <param name="offset">Starting file offset</param>
		/// <param name="rva">Starting RVA</param>
		/// <param name="fileAlignment">File alignment</param>
		/// <param name="sectionAlignment">Section alignment</param>
		protected void CalculateRvasAndFileOffsets(List<IChunk> chunks, FileOffset offset, RVA rva, uint fileAlignment, uint sectionAlignment) {
			foreach (var chunk in chunks) {
				chunk.SetOffset(offset, rva);
				uint len = chunk.GetLength();
				offset += len;
				rva += len;
				offset = offset.AlignUp(fileAlignment);
				rva = rva.AlignUp(sectionAlignment);
			}
		}

		/// <summary>
		/// Writes all chunks to <paramref name="writer"/>
		/// </summary>
		/// <param name="writer">The writer</param>
		/// <param name="chunks">All chunks</param>
		/// <param name="offset">File offset of first chunk</param>
		/// <param name="fileAlignment">File alignment</param>
		protected void WriteChunks(BinaryWriter writer, List<IChunk> chunks, FileOffset offset, uint fileAlignment) {
			foreach (var chunk in chunks) {
				chunk.VerifyWriteTo(writer);
				offset += chunk.GetLength();
				var newOffset = offset.AlignUp(fileAlignment);
				writer.WriteZeros((int)(newOffset - offset));
				offset = newOffset;
			}
		}

		/// <inheritdoc/>
		void IMetaDataListener.OnMetaDataEvent(MetaData metaData, MetaDataEvent evt) {
			switch (evt) {
			case MetaDataEvent.BeginCreateTables:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDBeginCreateTables);
				break;

			case MetaDataEvent.MemberDefRidsAllocated:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDMemberDefRidsAllocated);
				break;

			case MetaDataEvent.MemberDefsInitialized:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDMemberDefsInitialized);
				break;

			case MetaDataEvent.MostTablesSorted:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDMostTablesSorted);
				break;

			case MetaDataEvent.MemberDefCustomAttributesWritten:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDMemberDefCustomAttributesWritten);
				break;

			case MetaDataEvent.BeginWriteMethodBodies:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDBeginWriteMethodBodies);
				break;

			case MetaDataEvent.EndWriteMethodBodies:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDEndWriteMethodBodies);
				break;

			case MetaDataEvent.BeginAddResources:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDBeginAddResources);
				break;

			case MetaDataEvent.EndAddResources:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDEndAddResources);
				break;

			case MetaDataEvent.OnAllTablesSorted:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDOnAllTablesSorted);
				break;

			case MetaDataEvent.EndCreateTables:
				Listener.OnWriterEvent(this, ModuleWriterEvent.MDEndCreateTables);
				break;

			default:
				break;
			}
		}

		ILogger GetLogger() {
			return TheOptions.Logger ?? DummyLogger.ThrowModuleWriterExceptionOnErrorInstance;
		}

		/// <inheritdoc/>
		void ILogger.Log(object sender, LoggerEvent loggerEvent, string format, params object[] args) {
			GetLogger().Log(this, loggerEvent, format, args);
		}

		/// <inheritdoc/>
		bool ILogger.IgnoresEvent(LoggerEvent loggerEvent) {
			return GetLogger().IgnoresEvent(loggerEvent);
		}
	}
}