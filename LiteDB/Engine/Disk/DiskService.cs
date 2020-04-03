﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement custom fast/in memory mapped disk access
    /// [ThreadSafe]
    /// </summary>
    internal class DiskService : IDisposable
    {
        private readonly MemoryCache _cache;
        private readonly DiskWriterQueue _queue;

        private IStreamFactory _streamFactory;

        private StreamPool _streamPool;

        private HeaderPage _header;

        private long _logStartPosition;
        private long _logEndPosition;

        public DiskService(EngineSettings settings, int[] memorySegmentSizes)
        {
            _cache = new MemoryCache(memorySegmentSizes);

            // get new stream factory based on settings
            _streamFactory = settings.CreateDataFactory();

            var isNew = _streamFactory.Exists() == false;

            // create stream pool
            _streamPool = new StreamPool(_streamFactory, settings.ReadOnly);

            // create async writer queue for log file
            _queue = new DiskWriterQueue(_streamPool.Writer);

            // create new database if not exist yet
            if (isNew)
            {
                LOG($"creating new database: '{Path.GetFileName(_streamFactory.Name)}'", "DISK");

                _header = this.Initialize(_streamPool.Writer, settings.Collation, settings.InitialSize);
            }
            else
            {
                // load header page from position 0 from file
                var stream = _streamPool.Rent();
                var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);

                try
                {
                    stream.Position = 0;
                    stream.Read(buffer.Array, 0, PAGE_SIZE);

                    // if first byte are 1 this datafile are encrypted but has do defined password to open
                    if (buffer[0] == 1) throw new LiteException(0, "This data file is encrypted and needs a password to open");

                    _header = new HeaderPage(buffer);
                }
                finally
                {
                    _streamPool.Return(stream);
                }
            }

            _logStartPosition = (_header.LastPageID + 1) * PAGE_SIZE;
            _logEndPosition = _streamFactory.GetLength();
        }

        /// <summary>
        /// Get async queue writer
        /// </summary>
        public DiskWriterQueue Queue => _queue;

        /// <summary>
        /// Get memory cache instance
        /// </summary>
        public MemoryCache Cache => _cache;

        /// <summary>
        /// Get writer Stream single instance
        /// </summary>
        public Stream Writer => _streamPool.Writer;

        /// <summary>
        /// Get stream factory instance;
        /// </summary>
        public IStreamFactory Factory => _streamFactory;

        /// <summary>
        /// Get header page single database instance
        /// </summary>
        public HeaderPage Header => _header;

        /// <summary>
        /// Get log length
        /// </summary>
        public long LogLength => _logEndPosition - _logStartPosition;

        /// <summary>
        /// Create a new empty database (use synced mode)
        /// </summary>
        private HeaderPage Initialize(Stream stream, Collation collation, long initialSize)
        {
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var header = new HeaderPage(buffer, 0);

            // update collation
            header.Pragmas.Set(Pragmas.COLLATION, (collation ?? Collation.Default).ToString(), false);

            // update buffer
            header.UpdateBuffer();

            stream.Write(buffer.Array, buffer.Offset, PAGE_SIZE);

            if (initialSize > 0)
            {
                stream.SetLength(initialSize);
            }

            stream.FlushToDisk();

            return header;
        }

        /// <summary>
        /// Get a new instance for read data/log pages. This instance are not thread-safe - must request 1 per thread (used in Transaction)
        /// </summary>
        public DiskReader GetReader()
        {
            return new DiskReader(_cache, _streamPool);
        }

        /// <summary>
        /// Write pages inside file origin using async queue - WORKS ONLY FOR LOG FILE - returns how many pages are inside "pages"
        /// </summary>
        public int WriteAsync(IEnumerable<PageBuffer> pages)
        {
            var count = 0;

            foreach (var page in pages)
            {
                ENSURE(page.ShareCounter == BUFFER_WRITABLE, "to enqueue page, page must be writable");

                // adding this page into file AS new page (at end of file)
                // must add into cache to be sure that new readers can see this page
                page.Position = (Interlocked.Add(ref _logEndPosition, PAGE_SIZE)) - PAGE_SIZE;

                // mark this page as readable and get cached paged to enqueue
                var readable = _cache.MoveToReadable(page);

                _queue.EnqueuePage(readable);

                count++;
            }

            _queue.Run();

            return count;
        }

        /// <summary>
        /// Clear data file, close any data stream pool, change password and re-create data factory
        /// </summary>
        public void ChangePassword(string password, EngineSettings settings)
        {
            if (settings.Password == password) return;

            throw new NotImplementedException();
            /*
            // empty data file
            this.SetLength(0, FileOrigin.Data);

            // close all streams
            _dataPool.Dispose();
            
            // delete data file
            _dataFactory.Delete();

            settings.Password = password;

            // new datafile will be created with new password
            _dataFactory = settings.CreateDataFactory();

            // create stream pool
            _dataPool = new StreamPool(_dataFactory, false);

            // get initial data file length
            _dataLength = - PAGE_SIZE;
            */
        }

        #region Sync Read/Write operations

        /// <summary>
        /// Read all log from current log position to end of file. 
        /// This operation are sync and should not be run with any page on queue
        /// </summary>
        public IEnumerable<PageBuffer> ReadLog()
        {
            // do not use MemoryCache factory - reuse same buffer array (one page per time)
            // do not use BufferPool because header page can't be shared (byte[] is used inside page return)
            var buffer = new byte[PAGE_SIZE];

            var stream = _streamPool.Rent();

            ENSURE(_queue.Length == 0, "no pages on queue before read sync log");

            try
            {
                // get file length
                var length = _streamFactory.GetLength();

                // set to first log page position
                stream.Position = _logStartPosition;

                while (stream.Position < length)
                {
                    var position = stream.Position;

                    stream.Read(buffer, 0, PAGE_SIZE);

                    yield return new PageBuffer(buffer, 0, 0)
                    {
                        Position = position,
                        ShareCounter = 0
                    };
                }
            }
            finally
            {
                _streamPool.Return(stream);
            }
        }

        /// <summary>
        /// Write pages DIRECT in disk with NO queue. Used in CHECKPOINT only
        /// </summary>
        public void Write(IEnumerable<PageBuffer> pages)
        {
            var stream = _streamPool.Writer;

            foreach (var page in pages)
            {
                ENSURE(page.ShareCounter == 0, "this page can't be shared to use sync operation - do not use cached pages");

                stream.Position = page.Position;

                stream.Write(page.Array, page.Offset, PAGE_SIZE);
            }

            stream.FlushToDisk();
        }

        /// <summary>
        /// Reset log position at end of file (based on header.LastPageID) and shrink file
        /// </summary>
        public void ResetLogPosition(bool shrink)
        {
            _logStartPosition = (_header.LastPageID + 1) * PAGE_SIZE;
            _logEndPosition = _header.LastPageID * PAGE_SIZE; // always 1 PAGE_SIZE beause incremenets before use

            if (shrink)
            {
                _streamPool.Writer.SetLength(_logStartPosition);
            }
        }

        #endregion

        public void Dispose()
        {
            // dispose queue (wait finish)
            _queue.Dispose();

            // dispose Stream pools
            _streamPool.Dispose();

            // other disposes
            _cache.Dispose();
        }
    }
}
