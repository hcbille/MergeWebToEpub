﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace MergeWebToEpub
{
    /// <summary>
    /// The functions used to combine Epubs
    /// </summary>
    public class EpubCombiner
    {
        public EpubCombiner(Epub initial)
        {
            this.InitialEpub = initial;
        }

        public void Add(Epub toAppend)
        {
            this.ToAppend = toAppend;
            Combine();
        }

        public void Combine()
        {
            /*
             * Calculate new names of XHTML files
            Copy HTML files, fixing up any hyperlinks and img tags.  Make note of images that are in use.
            Copy images (with new names) that are in use.  Ignore images that are no longer being used or are duplictes
            Add entries to manifest and table of contents.  Notes
            There may be two table of contents if it's an epub 3
            Don't bother copying stylesheet
             */

            NewAbsolutePaths.Clear();
            NewItemIds.Clear();

            CalculateNewPathsAndIds();
            foreach (var item in ToAppend.Opf.Manifest)
            {
                CopyEpubItem(item);
            }
            CopyTableOfContents();
            CopySpine();
        }

        public void CalculateNewPathsAndIds()
        {
            var pages = InitialEpub.Opf.GetPageItems();
            int maxPrefix = GetMaxPrefix(pages);
            foreach(var p in ToAppend.Opf.GetPageItems())
            {
                CalcNewPathAndID(p, maxPrefix + 1);
            }

            var images = InitialEpub.Opf.GetImageItems();
            maxPrefix = GetMaxPrefix(images);
            foreach (var i in ToAppend.Opf.GetImageItems())
            {
                CalcNewPathAndID(i, maxPrefix + 1);
            }
        }

        public int GetMaxPrefix(List<EpubItem> items)
        {
            int maxPrefix = 0;
            foreach(var item in items)
            {
                var prefix = PrefixAsInt(item.AbsolutePath);
                maxPrefix = Math.Max(maxPrefix, Convert.ToInt32(prefix));
            }
            return maxPrefix;
        }

        public void CalcNewPathAndID(EpubItem item, int offset)
        {
            string oldAbsolutePath = item.AbsolutePath;
            var olfFileName = oldAbsolutePath.getZipFileName();
            var oldprefix = PrefixAsInt(olfFileName);
            var fileName = StripPrefixFromFileName(olfFileName);
            var path = oldAbsolutePath.GetZipPath();
            if (!string.IsNullOrEmpty(path))
            {
                path += '/';
            }

            // note possible conflict as "cover" does not have a prefix
            // which might conflict if there was also a page with 0000_cover.xhtml
            // So, if page has a prefix, bump offest by one.
            var bump = ExtractPrefixFromFileName(olfFileName) == null ? 0 : 1;

            var newPrefix = (oldprefix + offset + bump).ToString("D4");
            var newAbsolutePath = $"{path}{newPrefix}_{fileName}";
            NewAbsolutePaths.Add(oldAbsolutePath, newAbsolutePath);

            string newId = StripDigits(item.Id) + newPrefix;
            NewItemIds.Add(item.Id, newId);
        }

        public string StripDigits(string oldId)
        {
            var prefix = new StringBuilder();
            foreach (var c in oldId)
            {
                if (!Char.IsDigit(c))
                {
                    prefix.Append(c);
                }
            }
            return prefix.ToString();
        }

        public int PrefixAsInt(string absolutePath)
        {
            string prefixString = ExtractPrefixFromFileName(absolutePath);
            return string.IsNullOrEmpty(prefixString) ? 0 : Convert.ToInt32(prefixString);
        }

        /// <summary>
        /// Assumes file has a four digit numeric prefix, followed by an underscore
        /// </summary>
        /// <param name="absolutePath"></param>
        /// <returns></returns>
        public string ExtractPrefixFromFileName(string absolutePath)
        {
            var fileName = absolutePath.getZipFileName();
            return ((5 < fileName.Length) && (fileName[4] == '_'))
                ? fileName.Substring(0, 4)
                : null;
        }

        public string StripPrefixFromFileName(string fileName)
        {
            return ((5 < fileName.Length) && (fileName[4] == '_'))
                ? fileName.Substring(5, fileName.Length - 5)
                : fileName;
        }

        public void CopyEpubItem(EpubItem item)
        {
            if (item.IsXhtmlPage)
            {
                CopyEpubItem(item, (i) => UpdateXhtmlPage(i));
            }
            else if (item.IsImage)
            {
                CopyEpubItem(item, (i) => i.RawBytes);
            }
            // else don't copy
        }

        public void CopyEpubItem(EpubItem item, Func<EpubItem, byte[]>docUpdater)
        {
            var newItem = new EpubItem()
            {
                Id = NewItemIds[item.Id],
                AbsolutePath = NewAbsolutePaths[item.AbsolutePath],
                MediaType = item.MediaType,
                RawBytes = docUpdater(item)
            };
            string source = null;
            ToAppend.Opf.Metadata.Sources.TryGetValue(item.MetadataId, out source);
            InitialEpub.Opf.AppendItem(newItem, source);
        }

        public byte[] UpdateXhtmlPage(EpubItem item)
        {
            System.Diagnostics.Trace.WriteLine($"Fixing up page {item.AbsolutePath}");
            var xhtml = item.RawBytes.ToXhtml();
            var itemPath = item.AbsolutePath.GetZipPath();
            FixupReferences(xhtml, itemPath);
            return xhtml.ToStream().ToArray();
        }

        public void FixupReferences(XDocument doc, string itemPath)
        {
            FixupReferences(doc, Epub.svgNs + "image", Epub.xlinkNs + "href", itemPath);
            FixupReferences(doc, Epub.xhtmlNs + "img", "src", itemPath);
            FixupReferences(doc, Epub.xhtmlNs + "a", "href", itemPath);
            // ToDo, <link> tags
        }

        public void FixupReferences(XDocument doc, XName element, XName attributeName, string itemPath)
        {
            foreach(var e in doc.Root.Descendants(element))
            {
                var attrib = e.Attribute(attributeName);
                if (attrib != null)
                {
                    attrib.Value = FixupUrl(attrib.Value, itemPath);
                }
            }
        }

        public string FixupUrl(string uri, string itemPath)
        {
            // special case, it's a link to anchor on same page
            if (uri[0] == '#')
            {
                return uri;
            }

            // internal URLs are relative, so, if not relative
            // leave it alone
            Uri testUrl = null;
            if (!Uri.TryCreate(uri, UriKind.Relative, out testUrl))
            {
                return uri;
            }

            var fragments = uri.Split(new char[] { '#' });
            var path = fragments[0];
            var urlAbsolutePath = ZipUtils.RelativePathToAbsolute(itemPath, path);
            var newAbsolutePath = NewAbsolutePaths[urlAbsolutePath];
            var newRelativePath = ZipUtils.AbsolutePathToRelative(itemPath, newAbsolutePath);
            if (2 == fragments.Length)
            {
                newRelativePath += "#" + fragments[1];
            }
            return newRelativePath;
        }

        public void CopyTableOfContents()
        {
            var newTocEntries = CopyTocEntries(ToAppend.ToC.Entries);
            InitialEpub.ToC.Entries.AddRange(newTocEntries);
        }

        public List<TocEntry> CopyTocEntries(List<TocEntry> entries)
        {
            return entries
                .Select(entry => CopyTocEntry(entry))
                .ToList();
        }

        public TocEntry CopyTocEntry(TocEntry entry)
        {
            return new TocEntry()
            {
                Title = entry.Title,
                ContentSrc = NewAbsolutePaths[entry.ContentSrc],
                Children = CopyTocEntries(entry.Children)
            };
        }

        public void CopySpine()
        {
            InitialEpub.Opf.Spine.AddRange(
                ToAppend.Opf.Spine.Select(s => NewItemIds[s])
            );
        }

        public Epub InitialEpub { get; set; }
        public Epub ToAppend { get; set; }

        /// <summary>
        /// Map Item's old Absolute path to new Absolute path
        /// </summary>
        public Dictionary<string, string> NewAbsolutePaths { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Map Item's old IDs to new IDs
        /// </summary>
        public Dictionary<string, string> NewItemIds { get; set; } = new Dictionary<string, string>();
    }
}
