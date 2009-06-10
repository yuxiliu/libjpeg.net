﻿/* Copyright (C) 2008-2009, Bit Miracle
 * http://www.bitmiracle.com
 * 
 * Copyright (C) 1994-1996, Thomas G. Lane.
 * This file is part of the Independent JPEG Group's software.
 * For conditions of distribution and use, see the accompanying README file.
 *
 */

/*
 * This file contains a command-line user interface for the JPEG decompressor.
 *
 * To simplify script writing, the "-outfile" switch is provided.  The syntax
 *  djpeg [options]  -outfile outputfile  inputfile
 * works regardless of which command line style is used.
 */

//#define USE_CLASSIC_API

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

using LibJpeg;
using LibJpeg.Classic;
using cdJpeg;

namespace dJpeg
{
    public class Program
    {
        static bool printed_version = false;
        static IMAGE_FORMATS requested_fmt;
        static string progname;    /* program name for error messages */
        static string outfilename;   /* for -outfile switch */

#if USE_CLASSIC_API

        public static void Main(string[] args)
        {
            progname = Path.GetFileName(Environment.GetCommandLineArgs()[0]);

            /* Initialize the JPEG decompression object with default error handling. */
            cd_jpeg_error_mgr err = new cd_jpeg_error_mgr();
            jpeg_decompress_struct cinfo = new jpeg_decompress_struct(err);

            /* Insert custom marker processor for COM and APP12.
             * APP12 is used by some digital camera makers for textual info,
             * so we provide the ability to display it as text.
             * If you like, additional APPn marker types can be selected for display,
             * but don't try to override APP0 or APP14 this way (see libjpeg.doc).
             */
            cinfo.jpeg_set_marker_processor((int)JPEG_MARKER.M_COM, new jpeg_decompress_struct.jpeg_marker_parser_method(print_text_marker));
            cinfo.jpeg_set_marker_processor((int)JPEG_MARKER.M_APP0 + 12, print_text_marker);

            /* Scan command line to find file names. */
            /* It is convenient to use just one switch-parsing routine, but the switch
             * values read here are ignored; we will rescan the switches after opening
             * the input file.
             * (Exception: tracing level set here controls verbosity for COM markers
             * found during jpeg_read_header...)
             */
            int file_index;
            bool parsedOK = parse_switches(cinfo, args, false, out file_index);

            /* Must have either -outfile switch or explicit output file name */
            if (outfilename == null)
            {
                // file_index should point to input file 
                if (file_index != args.Length - 2)
                {
                    Console.WriteLine(string.Format("{0}: must name one input and one output file.", progname));
                    usage();
                    return;
                }

                // output file comes right after input one
                outfilename = args[file_index + 1];
            }
            else
            {
                // file_index should point to input file
                if (file_index != args.Length - 1)
                {
                    Console.WriteLine(string.Format("{0}: must name one input and one output file.", progname));
                    usage();
                    return;
                }
            }

            /* Open the input file. */
            FileStream input_file = null;
            if (file_index < args.Length)
            {
                try
                {
                    input_file = new FileStream(args[file_index], FileMode.Open);
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("{0}: can't open {1}", progname, args[file_index]));
                    Console.WriteLine(e.Message);
                    return;
                }
            }
            else
            {
                Console.WriteLine(string.Format("{0}: sorry, can't read file from console"));
                return;
            }

            /* Open the output file. */
            FileStream output_file = null;
            if (outfilename != null)
            {
                try
                {
                    output_file = new FileStream(outfilename, FileMode.Create);
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("{0}: can't open {1}", progname, args[file_index]));
                    Console.WriteLine(e.Message);
                    return;
                }
            }
            else
            {
                Console.WriteLine(string.Format("{0}: sorry, can't write file to console"));
                return;
            }

            /* Specify data source for decompression */
            cinfo.jpeg_stdio_src(input_file);

            /* Read file header, set default decompression parameters */
            cinfo.jpeg_read_header(true);

            /* Adjust default decompression parameters by re-parsing the options */
            parse_switches(cinfo, args, true, out file_index);

            /* Initialize the output module now to let it override any crucial
             * option settings (for instance, GIF wants to force color quantization).
             */
            djpeg_dest_struct dest_mgr = null;

            switch (requested_fmt)
            {
                case IMAGE_FORMATS.FMT_BMP:
                    dest_mgr = new bmp_dest_struct(cinfo, false);
                    break;
                case IMAGE_FORMATS.FMT_OS2:
                    dest_mgr = new bmp_dest_struct(cinfo, true);
                    break;
                default:
                    cinfo.ERREXIT((int)ADDON_MESSAGE_CODE.JERR_UNSUPPORTED_FORMAT);
                    break;
            }

            dest_mgr.output_file = output_file;

            /* Start decompressor */
            cinfo.jpeg_start_decompress();

            /* Write output file header */
            dest_mgr.start_output();

            /* Process data */
            while (cinfo.Output_scanline < cinfo.Output_height)
            {
                uint num_scanlines = cinfo.jpeg_read_scanlines(dest_mgr.buffer, dest_mgr.buffer_height);
                dest_mgr.put_pixel_rows(num_scanlines);
            }

            /* Finish decompression and release memory.
             * I must do it in this order because output module has allocated memory
             * of lifespan JPOOL_IMAGE; it needs to finish before releasing memory.
             */
            dest_mgr.finish_output();
            cinfo.jpeg_finish_decompress();

            /* Close files, if we opened them */
            input_file.Close();
            input_file.Dispose();

            output_file.Close();
            output_file.Dispose();

            /* All done. */
            if (cinfo.Err.Num_warnings != 0)
                Console.WriteLine("Corrupt-data warning count is not zero");
        }

        /// <summary>
        /// Marker processor for COM and interesting APPn markers.
        /// This replaces the library's built-in processor, which just skips the marker.
        /// We want to print out the marker as text, to the extent possible.
        /// Note this code relies on a non-suspending data source.
        /// </summary>
        static bool print_text_marker(jpeg_decompress_struct cinfo)
        {
            bool traceit = (cinfo.Err.Trace_level >= 1);
            
            int length = (int)(jpeg_getc(cinfo) << 8);
            length += (int)jpeg_getc(cinfo);
            length -= 2;            /* discount the length word itself */

            if (traceit)
            {
                if (cinfo.Unread_marker == (int)JPEG_MARKER.M_COM)
                {
                    Console.WriteLine("Comment, length {0}:", length);
                }
                else
                {
                    /* assume it is an APPn otherwise */
                    Console.WriteLine("APP{0}, length {1}:", cinfo.Unread_marker - JPEG_MARKER.M_APP0, length);
                }
            }

            uint lastch = 0;
            while (--length >= 0)
            {
                uint ch = (uint)jpeg_getc(cinfo);
                if (traceit)
                {
                    /* Emit the character in a readable form.
                     * Nonprintables are converted to \nnn form,
                     * while \ is converted to \\.
                     * Newlines in CR, CR/LF, or LF form will be printed as one newline.
                     */
                    if (ch == '\r')
                    {
                        Console.WriteLine();
                    }
                    else if (ch == '\n')
                    {
                        if (lastch != '\r')
                            Console.WriteLine();
                    }
                    else if (ch == '\\')
                    {
                        Console.Write("\\\\");
                    }
                    else if (!Char.IsControl((char)ch))
                    {
                        Console.Write(ch);
                    }
                    else
                    {
                        Console.Write(encodeOctalString(ch));
                    }

                    lastch = ch;
                }
            }

            if (traceit)
                Console.WriteLine();
                
            return true;
        }

        /// <summary>
        /// Parse optional switches.
        /// Returns argv[] index of first file-name argument (== argc if none).
        /// Any file names with indexes <= last_file_arg_seen are ignored;
        /// they have presumably been processed in a previous iteration.
        /// (Pass 0 for last_file_arg_seen on the first or only iteration.)
        /// for_real is false on the first (dummy) pass; we may skip any expensive
        /// processing.
        /// </summary>
        static bool parse_switches(jpeg_decompress_struct cinfo, string[] argv, bool for_real, out int last_file_arg_seen)
        {
            string arg;

            /* Set up default JPEG parameters. */
            requested_fmt = IMAGE_FORMATS.FMT_BMP;    /* set default output file format */
            outfilename = null;
            last_file_arg_seen = -1;
            cinfo.Err.Trace_level = 0;

            /* Scan command line options, adjust parameters */
            int argn = 0;
            for ( ; argn < argv.Length; argn++)
            {
                arg = argv[argn];
                if (arg[0] != '-')
                {
                    /* Not a switch, must be a file name argument */
                    last_file_arg_seen = argn;
                    break;
                }

                arg = arg.Substring(1);

                if (cdjpeg_utils.keymatch(arg, "bmp", 1))
                {
                    /* BMP output format. */
                    requested_fmt = IMAGE_FORMATS.FMT_BMP;
                }
                else if (cdjpeg_utils.keymatch(arg, "colors", 1) ||
                         cdjpeg_utils.keymatch(arg, "colours", 1) ||
                         cdjpeg_utils.keymatch(arg, "quantize", 1) ||
                         cdjpeg_utils.keymatch(arg, "quantise", 1))
                {
                    /* Do color quantization. */
                    int val;

                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    try
                    {
                        val = int.Parse(argv[argn]);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }

                    cinfo.Desired_number_of_colors = val;
                    cinfo.Quantize_colors = true;
                }
                else if (cdjpeg_utils.keymatch(arg, "dct", 2))
                {
                    /* Select IDCT algorithm. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    if (cdjpeg_utils.keymatch(argv[argn], "int", 1))
                    {
                        cinfo.Dct_method = J_DCT_METHOD.JDCT_ISLOW;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "fast", 2))
                    {
                        cinfo.Dct_method = J_DCT_METHOD.JDCT_IFAST;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "float", 2))
                    {
                        cinfo.Dct_method = J_DCT_METHOD.JDCT_FLOAT;
                    }
                    else
                    {
                        usage();
                        return false;
                    }
                }
                else if (cdjpeg_utils.keymatch(arg, "dither", 2))
                {
                    /* Select dithering algorithm. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    if (cdjpeg_utils.keymatch(argv[argn], "fs", 2))
                    {
                        cinfo.Dither_mode = J_DITHER_MODE.JDITHER_FS;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "none", 2))
                    {
                        cinfo.Dither_mode = J_DITHER_MODE.JDITHER_NONE;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "ordered", 2))
                    {
                        cinfo.Dither_mode = J_DITHER_MODE.JDITHER_ORDERED;
                    }
                    else
                    {
                        usage();
                        return false;
                    }
                }
                else if (cdjpeg_utils.keymatch(arg, "debug", 1) || cdjpeg_utils.keymatch(arg, "verbose", 1))
                {
                    /* Enable debug printouts. */
                    /* On first -d, print version identification */
                    if (!printed_version)
                    {
                        Console.Write(string.Format("Bit Miracle's DJPEG, version {0}\n{1}\n", jpeg_common_struct.Version, jpeg_common_struct.Copyright));
                        printed_version = true;
                    }
                    cinfo.Err.Trace_level++;
                }
                else if (cdjpeg_utils.keymatch(arg, "fast", 1))
                {
                    /* Select recommended processing options for quick-and-dirty output. */
                    cinfo.Two_pass_quantize = false;
                    cinfo.Dither_mode = J_DITHER_MODE.JDITHER_ORDERED;
                    if (!cinfo.Quantize_colors) /* don't override an earlier -colors */
                        cinfo.Desired_number_of_colors = 216;
                    cinfo.Dct_method = JpegConstants.JDCT_FASTEST;
                    cinfo.Do_fancy_upsampling = false;
                }
                else if (cdjpeg_utils.keymatch(arg, "grayscale", 2) || cdjpeg_utils.keymatch(arg, "greyscale", 2))
                {
                    /* Force monochrome output. */
                    cinfo.Out_color_space = J_COLOR_SPACE.JCS_GRAYSCALE;
                }
                else if (cdjpeg_utils.keymatch(arg, "maxmemory", 3))
                {
                    /* Maximum memory in Kb (or Mb with 'm'). */
                    long lval;

                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    try
                    {
                        string parsee = argv[argn].Remove(argv[argn].Length - 1);
                        lval = long.Parse(parsee);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }

                    if (argv[argn].EndsWith("m") || argv[argn].EndsWith("M"))
                        lval *= 1000L;
                }
                else if (cdjpeg_utils.keymatch(arg, "nosmooth", 3))
                {
                    /* Suppress fancy upsampling */
                    cinfo.Do_fancy_upsampling = false;
                }
                else if (cdjpeg_utils.keymatch(arg, "onepass", 3))
                {
                    /* Use fast one-pass quantization. */
                    cinfo.Two_pass_quantize = false;
                }
                else if (cdjpeg_utils.keymatch(arg, "os2", 3))
                {
                    /* BMP output format (OS/2 flavor). */
                    requested_fmt = IMAGE_FORMATS.FMT_OS2;
                }
                else if (cdjpeg_utils.keymatch(arg, "outfile", 4))
                {
                    /* Set output file name. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    outfilename = argv[argn];   /* save it away for later use */
                }
                else if (cdjpeg_utils.keymatch(arg, "scale", 1))
                {
                    /* Scale the output image by a fraction M/N. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    int slashPos = argv[argn].IndexOf('/');
                    if (slashPos == -1)
                    {
                        usage();
                        return false;
                    }

                    try
                    {
                        string num = argv[argn].Substring(0, slashPos);
                        cinfo.Scale_num = uint.Parse(num);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }

                    try
                    {
                        string denom = argv[argn].Substring(slashPos + 1);
                        cinfo.Scale_denom = uint.Parse(denom);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }
                }
                else
                {
                    /* bogus switch */
                    usage();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Read next byte
        /// </summary>
        static int jpeg_getc(jpeg_decompress_struct cinfo)
        {
            int v;
            if (!cinfo.Src.GetByte(out v))
                cinfo.ERREXIT((int)J_MESSAGE_CODE.JERR_CANT_SUSPEND);

            return v;
        }

#else

        //new version of Main
        public static void Main(string[] args)
        {
            progname = Path.GetFileName(Environment.GetCommandLineArgs()[0]);

            /* Initialize the JPEG decompression object with default error handling. */
            Decompressor decompressor = new Decompressor();

            /* Insert custom marker processor for COM and APP12.
             * APP12 is used by some digital camera makers for textual info,
             * so we provide the ability to display it as text.
             * If you like, additional APPn marker types can be selected for display,
             * but don't try to override APP0 or APP14 this way (see libjpeg.doc).
             */
            decompressor.SetMarkerProcessor((int)JPEG_MARKER.M_COM, printTextMarker);
            decompressor.SetMarkerProcessor((int)JPEG_MARKER.M_APP0 + 12, printTextMarker);

            /* Scan command line to find file names. */
            /* It is convenient to use just one switch-parsing routine, but the switch
             * values read here are ignored; we will rescan the switches after opening
             * the input file.
             * (Exception: tracing level set here controls verbosity for COM markers
             * found during jpeg_read_header...)
             */
            int file_index;
            bool parsedOK = parse_switches(decompressor, args, false, out file_index);

            /* Must have either -outfile switch or explicit output file name */
            if (outfilename == null)
            {
                // file_index should point to input file 
                if (file_index != args.Length - 2)
                {
                    Console.WriteLine(string.Format("{0}: must name one input and one output file.", progname));
                    usage();
                    return;
                }

                // output file comes right after input one
                outfilename = args[file_index + 1];
            }
            else
            {
                // file_index should point to input file
                if (file_index != args.Length - 1)
                {
                    Console.WriteLine(string.Format("{0}: must name one input and one output file.", progname));
                    usage();
                    return;
                }
            }

            /* Open the input file. */
            using (FileStream inputFile = openInputFile(file_index, args))
            {
                if (inputFile == null)
                    return;

                /* Open the output file. */
                using (FileStream outputFile = createOutputFile(file_index, args))
                {
                    if (outputFile == null)
                        return;

                    /* Specify data source for decompression */
                    decompressor.InputStream = inputFile;

                    /* Read file header, set default decompression parameters */
                    decompressor.ReadHeader(true);

                    /* Adjust default decompression parameters by re-parsing the options */
                    parse_switches(decompressor, args, true, out file_index);

                    /* Initialize the output module now to let it override any crucial
                     * option settings (for instance, GIF wants to force color quantization).
                     */
                    djpeg_dest_struct dest_mgr = null;
                    switch (requested_fmt)
                    {
                        case IMAGE_FORMATS.FMT_BMP:
                            dest_mgr = new bmp_dest_struct(decompressor, false);
                            break;
                        case IMAGE_FORMATS.FMT_OS2:
                            dest_mgr = new bmp_dest_struct(decompressor, true);
                            break;
                        default:
                            decompressor.ClassicDecompressor.ERREXIT((int)ADDON_MESSAGE_CODE.JERR_UNSUPPORTED_FORMAT);
                            break;
                    }

                    dest_mgr.output_file = outputFile;

                    /* Start decompressor */
                    decompressor.StartDecompress();

                    /* Write output file header */
                    dest_mgr.start_output();

                    /* Process data */
                    while (decompressor.OutputScanline < decompressor.OutputHeight)
                    {
                        int num_scanlines = decompressor.ReadScanlines(dest_mgr.buffer, (int)dest_mgr.buffer_height);
                        dest_mgr.put_pixel_rows((uint)num_scanlines);
                    }

                    /* Finish decompression and release memory.
                     * I must do it in this order because output module has allocated memory
                     * of lifespan JPOOL_IMAGE; it needs to finish before releasing memory.
                     */
                    dest_mgr.finish_output();
                    decompressor.FinishDecompress();
                }
            }
            
            /* All done. */
            if (decompressor.ClassicDecompressor.Err.Num_warnings != 0)
                Console.WriteLine("Corrupt-data warning count is not zero");
        }

        static FileStream openInputFile(int fileIndex, string[] args)
        {
            if (fileIndex >= args.Length)
            {
                Console.WriteLine(string.Format("{0}: sorry, can't read file from console"));
                return null;
            }

            try
            {
                return new FileStream(args[fileIndex], FileMode.Open);
            }
            catch (Exception e)
            {
                Console.WriteLine(string.Format("{0}: can't open {1}", progname, args[fileIndex]));
                Console.WriteLine(e.Message);
                return null;
            }
        }

        static FileStream createOutputFile(int fileIndex, string[] args)
        {
            if (outfilename == null)
            {
                Console.WriteLine(string.Format("{0}: sorry, can't write file to console"));
                return null;
            }

            try
            {
                return new FileStream(outfilename, FileMode.Create);
            }
            catch (Exception e)
            {
                Console.WriteLine(string.Format("{0}: can't open {1}", progname, args[fileIndex]));
                Console.WriteLine(e.Message);
                return null;
            }
        }

        /// <summary>
        /// Marker processor for COM and interesting APPn markers.
        /// This replaces the library's built-in processor, which just skips the marker.
        /// We want to print out the marker as text, to the extent possible.
        /// Note this code relies on a non-suspending data source.
        /// </summary>
        static bool printTextMarker(Decompressor decompressor)
        {
            bool traceit = (decompressor.ClassicDecompressor.Err.Trace_level >= 1);
            
            int length = (int)(jpeg_getc(decompressor) << 8);
            length += (int)jpeg_getc(decompressor);
            length -= 2;            /* discount the length word itself */

            if (traceit)
            {
                if (decompressor.UnreadMarker == (int)JPEG_MARKER.M_COM)
                {
                    Console.WriteLine("Comment, length {0}:", length);
                }
                else
                {
                    /* assume it is an APPn otherwise */
                    Console.WriteLine("APP{0}, length {1}:", decompressor.UnreadMarker - JPEG_MARKER.M_APP0, length);
                }
            }

            uint lastch = 0;
            while (--length >= 0)
            {
                uint ch = (uint)jpeg_getc(decompressor);
                if (traceit)
                {
                    /* Emit the character in a readable form.
                     * Nonprintables are converted to \nnn form,
                     * while \ is converted to \\.
                     * Newlines in CR, CR/LF, or LF form will be printed as one newline.
                     */
                    if (ch == '\r')
                    {
                        Console.WriteLine();
                    }
                    else if (ch == '\n')
                    {
                        if (lastch != '\r')
                            Console.WriteLine();
                    }
                    else if (ch == '\\')
                    {
                        Console.Write("\\\\");
                    }
                    else if (!Char.IsControl((char)ch))
                    {
                        Console.Write(ch);
                    }
                    else
                    {
                        Console.Write(encodeOctalString(ch));
                    }

                    lastch = ch;
                }
            }

            if (traceit)
                Console.WriteLine();
                
            return true;
        }

        /// <summary>
        /// Parse optional switches.
        /// Returns argv[] index of first file-name argument (== argc if none).
        /// Any file names with indexes <= last_file_arg_seen are ignored;
        /// they have presumably been processed in a previous iteration.
        /// (Pass 0 for last_file_arg_seen on the first or only iteration.)
        /// for_real is false on the first (dummy) pass; we may skip any expensive
        /// processing.
        /// </summary>
        static bool parse_switches(Decompressor decompressor, string[] argv, bool for_real, out int last_file_arg_seen)
        {
            string arg;

            /* Set up default JPEG parameters. */
            requested_fmt = IMAGE_FORMATS.FMT_BMP;    /* set default output file format */
            outfilename = null;
            last_file_arg_seen = -1;

            jpeg_decompress_struct cinfo = decompressor.ClassicDecompressor;
            cinfo.Err.Trace_level = 0;

            /* Scan command line options, adjust parameters */
            int argn = 0;
            for ( ; argn < argv.Length; argn++)
            {
                arg = argv[argn];
                if (arg[0] != '-')
                {
                    /* Not a switch, must be a file name argument */
                    last_file_arg_seen = argn;
                    break;
                }

                arg = arg.Substring(1);

                if (cdjpeg_utils.keymatch(arg, "bmp", 1))
                {
                    /* BMP output format. */
                    requested_fmt = IMAGE_FORMATS.FMT_BMP;
                }
                else if (cdjpeg_utils.keymatch(arg, "colors", 1) ||
                         cdjpeg_utils.keymatch(arg, "colours", 1) ||
                         cdjpeg_utils.keymatch(arg, "quantize", 1) ||
                         cdjpeg_utils.keymatch(arg, "quantise", 1))
                {
                    /* Do color quantization. */
                    int val;

                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    try
                    {
                        val = int.Parse(argv[argn]);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }

                    cinfo.Desired_number_of_colors = val;
                    cinfo.Quantize_colors = true;
                }
                else if (cdjpeg_utils.keymatch(arg, "dct", 2))
                {
                    /* Select IDCT algorithm. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    if (cdjpeg_utils.keymatch(argv[argn], "int", 1))
                    {
                        cinfo.Dct_method = J_DCT_METHOD.JDCT_ISLOW;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "fast", 2))
                    {
                        cinfo.Dct_method = J_DCT_METHOD.JDCT_IFAST;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "float", 2))
                    {
                        cinfo.Dct_method = J_DCT_METHOD.JDCT_FLOAT;
                    }
                    else
                    {
                        usage();
                        return false;
                    }
                }
                else if (cdjpeg_utils.keymatch(arg, "dither", 2))
                {
                    /* Select dithering algorithm. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    if (cdjpeg_utils.keymatch(argv[argn], "fs", 2))
                    {
                        cinfo.Dither_mode = J_DITHER_MODE.JDITHER_FS;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "none", 2))
                    {
                        cinfo.Dither_mode = J_DITHER_MODE.JDITHER_NONE;
                    }
                    else if (cdjpeg_utils.keymatch(argv[argn], "ordered", 2))
                    {
                        cinfo.Dither_mode = J_DITHER_MODE.JDITHER_ORDERED;
                    }
                    else
                    {
                        usage();
                        return false;
                    }
                }
                else if (cdjpeg_utils.keymatch(arg, "debug", 1) || cdjpeg_utils.keymatch(arg, "verbose", 1))
                {
                    /* Enable debug printouts. */
                    /* On first -d, print version identification */
                    if (!printed_version)
                    {
                        Console.Write(string.Format("Bit Miracle's DJPEG, version {0}\n{1}\n", jpeg_common_struct.Version, jpeg_common_struct.Copyright));
                        printed_version = true;
                    }
                    cinfo.Err.Trace_level++;
                }
                else if (cdjpeg_utils.keymatch(arg, "fast", 1))
                {
                    /* Select recommended processing options for quick-and-dirty output. */
                    cinfo.Two_pass_quantize = false;
                    cinfo.Dither_mode = J_DITHER_MODE.JDITHER_ORDERED;
                    if (!cinfo.Quantize_colors) /* don't override an earlier -colors */
                        cinfo.Desired_number_of_colors = 216;
                    cinfo.Dct_method = JpegConstants.JDCT_FASTEST;
                    cinfo.Do_fancy_upsampling = false;
                }
                else if (cdjpeg_utils.keymatch(arg, "grayscale", 2) || cdjpeg_utils.keymatch(arg, "greyscale", 2))
                {
                    /* Force monochrome output. */
                    cinfo.Out_color_space = J_COLOR_SPACE.JCS_GRAYSCALE;
                }
                else if (cdjpeg_utils.keymatch(arg, "maxmemory", 3))
                {
                    /* Maximum memory in Kb (or Mb with 'm'). */
                    long lval;

                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    try
                    {
                        string parsee = argv[argn].Remove(argv[argn].Length - 1);
                        lval = long.Parse(parsee);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }

                    if (argv[argn].EndsWith("m") || argv[argn].EndsWith("M"))
                        lval *= 1000L;
                }
                else if (cdjpeg_utils.keymatch(arg, "nosmooth", 3))
                {
                    /* Suppress fancy upsampling */
                    cinfo.Do_fancy_upsampling = false;
                }
                else if (cdjpeg_utils.keymatch(arg, "onepass", 3))
                {
                    /* Use fast one-pass quantization. */
                    cinfo.Two_pass_quantize = false;
                }
                else if (cdjpeg_utils.keymatch(arg, "os2", 3))
                {
                    /* BMP output format (OS/2 flavor). */
                    requested_fmt = IMAGE_FORMATS.FMT_OS2;
                }
                else if (cdjpeg_utils.keymatch(arg, "outfile", 4))
                {
                    /* Set output file name. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    outfilename = argv[argn];   /* save it away for later use */
                }
                else if (cdjpeg_utils.keymatch(arg, "scale", 1))
                {
                    /* Scale the output image by a fraction M/N. */
                    if (++argn >= argv.Length) /* advance to next argument */
                    {
                        usage();
                        return false;
                    }

                    int slashPos = argv[argn].IndexOf('/');
                    if (slashPos == -1)
                    {
                        usage();
                        return false;
                    }

                    try
                    {
                        string num = argv[argn].Substring(0, slashPos);
                        cinfo.Scale_num = uint.Parse(num);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }

                    try
                    {
                        string denom = argv[argn].Substring(slashPos + 1);
                        cinfo.Scale_denom = uint.Parse(denom);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        usage();
                        return false;
                    }
                }
                else
                {
                    /* bogus switch */
                    usage();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Read next byte
        /// </summary>
        static int jpeg_getc(Decompressor decompressor)
        {
            jpeg_decompress_struct cinfo = decompressor.ClassicDecompressor;

            int v;
            if (!cinfo.Src.GetByte(out v))
                cinfo.ERREXIT((int)J_MESSAGE_CODE.JERR_CANT_SUSPEND);

            return v;
        }

#endif

        private static string encodeOctalString(uint value)
        {
            //convert to int, for cleaner syntax below. 
            int x = (int)value;

            //return octal encoding \ddd of the character value. 
            return string.Format(
                @"\{0}{1}{2}",
                ((x >> 6) & 7),
                ((x >> 3) & 7),
                (x & 7)
            );
        }

        /// <summary>
        /// Complain about bad command line
        /// </summary>
        static void usage()
        {
            Console.Write("usage: {0} [switches] inputfile outputfile", progname);
            Console.WriteLine("Switches (names may be abbreviated):");
            Console.WriteLine("  -colors N      Reduce image to no more than N colors");
            Console.WriteLine("  -fast          Fast, low-quality processing");
            Console.WriteLine("  -grayscale     Force grayscale output");
            Console.WriteLine("  -scale M/N     Scale output image by fraction M/N, eg, 1/8");
            Console.WriteLine("  -os2           Select BMP output format (OS/2 style)");
            Console.WriteLine("Switches for advanced users:");
            Console.WriteLine("  -dct int       Use integer DCT method {0}", (JpegConstants.JDCT_DEFAULT == J_DCT_METHOD.JDCT_ISLOW ? " (default)" : ""));
            Console.WriteLine("  -dct fast      Use fast integer DCT (less accurate) {0}", (JpegConstants.JDCT_DEFAULT == J_DCT_METHOD.JDCT_IFAST ? " (default)" : ""));
            Console.WriteLine("  -dct float     Use floating-point DCT method {0}", (JpegConstants.JDCT_DEFAULT == J_DCT_METHOD.JDCT_FLOAT ? " (default)" : ""));
            Console.WriteLine("  -dither fs     Use F-S dithering (default)");
            Console.WriteLine("  -dither none   Don't use dithering in quantization");
            Console.WriteLine("  -dither ordered  Use ordered dither (medium speed, quality)");
            Console.WriteLine("  -map FILE      Map to colors used in named image file");
            Console.WriteLine("  -nosmooth      Don't use high-quality upsampling");
            Console.WriteLine("  -onepass       Use 1-pass quantization (fast, low quality)");
            Console.WriteLine("  -outfile name  Specify name for output file");
            Console.WriteLine("  -verbose  or  -debug   Emit debug output");
        }
    }
}
