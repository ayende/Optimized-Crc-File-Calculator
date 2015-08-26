# Optimized Crc File Calculator

This repository is used to host a small (less than 300 lines of code) 
command line tool for generating CRC values for all *.cs files in a set of directories
(specified as command line arguments).

For example (assuming you have http://github.com:ayende/ravendb downloaded to d:\ravendb), you can run it using:

PathCrcCalculator.exe d:\ravendb

It will think for a bit and then spit out all the files and their CRC values.

This code was built in order to be _fast_ and make maximum usage of the system resources. It is expected that users will run 
this tool on folders that contains (at least) thousands of files, and we need this to be as performant as possible.

In order to handle that, we have implemented an multi threaded Actor model:

* Setup a single thread to recursively read the directories and files from disk and to the backend processing actors.

* N threads (see below) to read the contents of the file from disk (which gets us parallel I/O). This is
  especially important when running on cloud machines where I/O resources are constrained (remote disks), but issuing
  multiple I/O requests at the same time can give us more I/O bandwidth.
  
* N threads (see below) to compute the CRC value from the file buffer (computing CRC value of the memory buffer is CPU bound,
  we can optimize calcuation time by parallizing computation). This along with the parallel I/O gives us maximum utilization
  of the machine resources.
  
>  Note, N threads is adaptive. We'll use at least 4 threads (so 1 for main execution, 1 for reading directories entries, 
> 4 for reading files, 4 for computing CRCs - total of 10 threads). But on machines that has more cores, we'll use more 
> threads, to get adaptive performance increase.

* In order to reduce allocations, we use a buffer pool to maintain a fixed (dynamic based on the actual usage in place) 
  set of buffers and try hard to avoid any unnecssary allocations.
  
* For example, we use native calls to ReadFile to avoid the overhead of using FileStream with its 4KB buffer allocation 
  per read.

## Something went wrong!

Despite our best efforts, this process is still too slow, and uses too much memory.
A simple way to reproduce this is to run this tool multiple times on the same directory. This will generate large amount of 
work to be done very easily.
Here is a sample command to show the issue (processing the same directory multiple times just to have a lot of data to expose the issue):

> Optimized.Crc.File.Calculator.exe d:\ravendb d:\ravendb d:\ravendb d:\ravendb d:\ravendb

This command will cause the program to consume about 225MB. It is possible to do (much) better. 

There are two tasks,and we'll like independent copies of those. 
We expect to get two separate pull requests from you, one for each task. You can try to solve both tasks at once, but it 
might be easier to solve them independently.

Note that you are free to make _any_ change in the code, including full re-architecturing of the system if you can do better.

Each pull request should include a detail explanation _why_ the changes were made and _how_ the solution work.

### Task #1 - Proper shutdown

The code currently has no way of shutting down without user involvement (who will notice that there are no more output and hit Enter in the console).
Gracefully shut down the program when it is done processing all the files in its input.

### Task #2 - Optimization.

The code uses too much memory, and takes too long. Reduce the amount of memory used. If you can, optimize the runtime for the program as well.
