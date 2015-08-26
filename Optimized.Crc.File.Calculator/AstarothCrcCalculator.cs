using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Optimized.Crc.File.Calculator
{
    public class AstarothCrcCalculator
    {
        // Using this native call avoid the need for the 4KB allocations on every file!
        [DllImport("Kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle handle, byte[] bytes, int numBytesToRead, out int numBytesRead, IntPtr mustBeZero);

        static void Main(string[] args)
        {
            BufferPool.GetBuffer(10000);
			// single threaded collection, we only use a single thread here
			var readDirectoryWork = new Queue<ReadDirectoryWork>();
			// queues for the various actors
			var crcComputationWork = new BlockingCollection<CrcComputationWork>();
            var readFileWork = new BlockingCollection<ReadFileWork>();

            SpinCrcComputations(crcComputationWork);

            SpinFileReaders(readFileWork, crcComputationWork);

			// Start the actors machinery by registering the initial directories
			foreach (var path in args)
			{
				readDirectoryWork.Enqueue(new ReadDirectoryWork
				{
					DirectoryName = path
				});
			}

            SpinDirectoryReader(readDirectoryWork, readFileWork);

          
            Console.WriteLine("Running computations...");
            Console.ReadLine();
        }

		private static void SpinDirectoryReader(Queue<ReadDirectoryWork> readDirectoryWork, BlockingCollection<ReadFileWork> readFileWork)
        {
            Task.Factory.StartNew(() =>
            {
                while (readDirectoryWork.Count > 0)
                {
                    var work = readDirectoryWork.Dequeue();
                    foreach (var directory in Directory.EnumerateDirectories(work.DirectoryName))
                    {
                        readDirectoryWork.Enqueue(new ReadDirectoryWork// more work for us!
                        {
                            DirectoryName = directory
                        });
                    }
                    foreach (var file in Directory.EnumerateFiles(work.DirectoryName, "*.cs"))
                    {
                        readFileWork.Add(new ReadFileWork// now we can let someone else handle this work
                        {
                            FileName = file
                        });
                    }
                }
            });
        }

        private static void SpinFileReaders(BlockingCollection<ReadFileWork> readFileWork, BlockingCollection<CrcComputationWork> crcComputationWork)
        {
            // These I/O Actors allows us to parallelize all the I/O work. On slow / cloud machines, that allows the I/O subsystem to get
            // bulk read speed from the underlying systems.
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        var work = readFileWork.Take();
                        var fileInfo = new FileInfo(work.FileName);
                        // some buffers can be very large, we will use consistent 16KB buffers
						// and if the file is too large, we will use multiple 16KB buffers.
						// For a file that is 260 KB (jQuery 1.8.3 is 261.46 KB) in size, that means we will use 272 KB only,
						// versus the 512KB we would use if we used a power of two approach.
						// So we'll waste only 12KB instead of 252KB
	                    var buffers = new List<byte[]>();
                        try
                        {
	                        const int bufferSize = 16*1024;
                            using (var fs = fileInfo.OpenRead())
                            {
	                            for (int j = 0; j < fileInfo.Length; j+=bufferSize)
	                            {
									var buffer = BufferPool.GetBuffer(bufferSize);
									buffers.Add(buffer);
									// we can't call fs.Read here, it will allocate a 4KB buffer that will then be discarded.
									// so we use the native ReadFile method and read directly to our pooled buffer
									int read;
									if (ReadFile(fs.SafeFileHandle, buffer, buffer.Length, out read, IntPtr.Zero) == false)
										throw new Win32Exception();
	                            }
                            }
                        }
                        catch (Exception)
                        {
                            // If there has ben any error in reading from the file or opening it, we've have
                            // to make sure that we won't forget about this buffer. Even with the error that just
                            // happened, we can still make use of it.
	                        foreach (var buffer in buffers)
	                        {
								BufferPool.ReturnBuffer(buffer);
	                        }
                            throw;
                        }
                        crcComputationWork.Add(new CrcComputationWork
                        {
                            Length = (int) fileInfo.Length,
							Buffers = buffers,// the SpinCrcComputations will be returning the bufer to the ppol
                            FileName = work.FileName
                        });
                    }
                });
            }
        }

        private static void SpinCrcComputations(BlockingCollection<CrcComputationWork> crcComputationWork)
        {
            // These computation actors will perform the actual CRC computation, we can 
            // probably optimize the CRC routine, but it is cheaper to just run it on multiple threads instead 
            // of going to near assembly in managed code.
            // Our costs are going to be in the I/O mostly, anyway.
            for (int i = 0; i < Math.Max(4, Environment.ProcessorCount); i++)
            {
                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        var work = crcComputationWork.Take();
                        try
                        {
                            var crc = work.Crc();
                            Console.WriteLine("{0} = {1:x}", work.FileName, crc);
                        }
                        finally
                        {
                            // Now it goes back to the pool, and can be used again
	                        foreach (var buffer in work.Buffers)
	                        {
								BufferPool.ReturnBuffer(buffer);
	                        }
                        }
                    }
                });
            }
        }

        public class ReadFileWork
        {
            public string FileName;
        }


        public class ReadDirectoryWork
        {
            public string DirectoryName;
        }

        public class CrcComputationWork
        {
	        public List<byte[]> Buffers;// use buffers from pool to avoid string allocations
            public int Length;

            public string FileName;
            public uint Crc()
            {
                var crc = 0xFFFFFFFF;
	            var currentBuffer = Buffers[0];
	            var bufferOffset = 0;
	            var bufferIndex = 0;
                for (var i = 0; i < Length; i++)
                {
	                if (i - bufferOffset >= currentBuffer.Length)
		                currentBuffer = Buffers[++bufferIndex];

					crc = crc ^ currentBuffer[i - bufferOffset];
					for (var j = 7; j >= 0; j--)
					{
						var mask = (uint)(-(crc & 1));
						crc = (crc >> 1) ^ (0xEDB88320 & mask);
					}
					i = i + 1;
                  
                }
                return ~crc;
            }
        }

        public static class BufferPool
        {
			// We use a thread local buffer store here, the idea is that we don't want multiple threads
			// to compete among themselves for the buffers, because we want to achieve the maximum amount
			// of concurrency possible. A single shared buffer pool would result in us having to do either
			// costly synchronization or using complex concurrent operations. This is simpler and cheaper.
			// It also has the advantage of ensuring locality. That is, a buffer that was returned to the pool
			// will be used in the same thread, and is likely to reside on the same CPU core cache line, ensuring
			// faster performance.
			//
            // We use an array of 32 stacks of byte arrays. Each stack in the array correspond
            // to a set of buffers that match that array index by power of two. So the buffers in 
            // index 0 have size of 1, index 1 has size of 2, index 2 has size of 4, index 14
            // has 16KB, etc...
            // 
            // We use a stack to make sure that the buffere we hand out are the fresh ones (the ones most
            // likely to still reside close to the CPU).
            private static readonly ThreadLocal<Stack<byte[]>[]> _buffersBySize =
                new ThreadLocal<Stack<byte[]>[]>(() => new Stack<byte[]>[32]);

            // We can just cache that once, since it is immutable.
            private static readonly byte[] _emptyBuffer = new byte[0];

            public static byte[] GetBuffer(int requestedSize)
            {
                if (requestedSize <= 0)
                    return _emptyBuffer;

                var actualSize = NearestPowerOfTwo(requestedSize);
                var pos = MostSignificantBit(actualSize);

                if (_buffersBySize.Value[pos] == null)
                    _buffersBySize.Value[pos] = new Stack<byte[]>();

                if (_buffersBySize.Value[pos].Count == 0)
                    return new byte[actualSize];// have to create a new buffer :-(

                return _buffersBySize.Value[pos].Pop();
            }

            public static void ReturnBuffer(byte[] buffer)
            {
                if (buffer.Length == 0)
                    return;

                var actualSize = NearestPowerOfTwo(buffer.Length);
                if (actualSize != buffer.Length)
                    return; // can't put a buffer of strange size here (probably an error)

                var pos = MostSignificantBit(actualSize);

                if (_buffersBySize.Value[pos] == null)
                    _buffersBySize.Value[pos] = new Stack<byte[]>();


               _buffersBySize.Value[pos].Push(buffer);
            }


            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int NearestPowerOfTwo(int v)
            {
                v--;
                v |= v >> 1;
                v |= v >> 2;
                v |= v >> 4;
                v |= v >> 8;
                v |= v >> 16;
                v++;
                return v;

            }
            private static int MostSignificantBit(int myInt)
            {
                int mask = 1 << 31;
                for (int bitIndex = 31; bitIndex >= 0; bitIndex--)
                {
                    if ((myInt & mask) != 0)
                    {
                        return bitIndex;
                    }
                    mask >>= 1;
                }
                return -1;
            }
        }
    }
}
