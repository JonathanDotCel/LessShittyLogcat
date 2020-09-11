﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.


// Hi!
// Unfortunately I had just 2 days to finish this, document it and get it out the door
// As such there's a strange mishmash of code and obsolete naming conventions.
// This will get some love and attention going forward, thanks.
// 


using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Diagnostics;
using System.Windows.Threading;
using System.Linq;

namespace LessShittyLogcat {
	
	// Can a message match any filter, or all?
	public enum FilterMode{ ANY, ALL }

	
	public class LogEntry{
		public string level { get; set; }
		public string time { get; set; }
		public string PID { get; set; }
		public string TID { get; set; }
		public string app { get; set; }		// not bound
		public string tag { get; set; }
		public string text {get; set;}
		public string raw { get; set; }		// un-split text
		public Brush Color { get; set; }
	}

	public class FilterGroup{		
		public CheckBox checkBox;
		public TextBox textBox;
		public bool isExclusion;
		public bool isEnabled => checkBox.IsChecked.GetValueOrDefault();
		public string text => textBox.Text;
		public override string ToString()
		{
			return "GRP " + checkBox + " / " + textBox + "(" + text + ")";
		}
	}

	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window {

		//public event PropertyChangedEventHandler PropertyChanged;
		
		const int MAX_FILTERS = 4;

		public List<FilterGroup> inclusionGroup = new List<FilterGroup>();
		public List<FilterGroup> exclusionGroup = new List<FilterGroup>();
		public List<FilterGroup> allGroups = new List<FilterGroup>();  // inclusiongroup + exclusiongroup

		private bool FilterEnabled( int which ){ return inclusionGroup[which].isEnabled; }
		private string FilterAt( int which ){ return inclusionGroup[which].text; }

		private bool ExclusionEnabled( int which ){ return exclusionGroup[which].isEnabled; }
		private string ExclusionAt( int which ){ return exclusionGroup[which].text; }

		// Cache the value to cut down on per-line checks
		private bool _anyFilterEnabled = false;
		private bool anyFilterEnabled{get{
			for (int i = 0; i < MAX_FILTERS; i++){
					if (FilterEnabled(i) && !string.IsNullOrEmpty( FilterAt(i) ))
					{
						_anyFilterEnabled = true;
						return true;
					}
				}
				_anyFilterEnabled = false;
				return false;
			}
		}

		// Storage between the event thread and UI thread
		// (this is the list that fills while we're paused)
		public List<LogEntry> pendingLogs = new List<LogEntry>();

		// Require all inclusions, or require any inclusions?
		public FilterMode filterMode = FilterMode.ANY;

		// Animate the filtered/unfiltered panel width
		public double splitterTarg = 0;
		public double splitterLerp = 50;
		public double gDelta => splitterTarg - splitterLerp;
		public bool animating => Math.Abs(gDelta) > 2;

		// Pour la dispatcher
		public delegate void GenericDelegate();
		public delegate void UpdateDelegate(Process logcatProcess);

		// Might as well keep it on the heap
		Process lastProcess;

		// For comparing timestamps / tags and grouping
		LogEntry lastAdded;

		// the integer-based checkboxes
		int wrapLength = 0;		
		int bufferSize = 1000;

		// These are thread-specific so let's cache them.
		// (Used for listview foregrounds)
		SolidColorBrush blackBrush;
		SolidColorBrush orangeBrush;
		SolidColorBrush redBrush;
		SolidColorBrush greenBrush;

		public MainWindow()
		{
			InitializeComponent();

			redBrush = new SolidColorBrush(Colors.DarkRed);
			greenBrush = new SolidColorBrush(Colors.DarkGreen);
			orangeBrush = new SolidColorBrush(Colors.DarkOrange);
			blackBrush = new SolidColorBrush(Colors.DarkBlue);

			// Could over engineer this for the sake of 6 lines.
			// ... or type it out.

			AddGroup( new FilterGroup(){ checkBox=cbFilter1, textBox=txtFilter1, isExclusion=false } );
			AddGroup( new FilterGroup(){ checkBox=cbFilter2, textBox=txtFilter2, isExclusion=false } );
			AddGroup( new FilterGroup(){ checkBox=cbFilter3, textBox=txtFilter3, isExclusion=false } );
			AddGroup( new FilterGroup(){ checkBox=cbFilter4, textBox=txtFilter4, isExclusion=false } );

			AddGroup( new FilterGroup() { checkBox=cbExclude1, textBox=txtExclude1, isExclusion=true } );
			AddGroup( new FilterGroup() { checkBox=cbExclude2, textBox=txtExclude2, isExclusion=true } );

			UpdateFilters(null,null);

			Dispatcher.BeginInvoke( new GenericDelegate( Animate ) );
			
			// to avoid a null PropertyChanged event
			this.DataContext = this;

			listBox1.Items.Add(new LogEntry() { time="Nowish", text = "Less Shitty Logcat is open source, MPL2.0 licensed. ", Color = blackBrush });
			listBox1.Items.Add(new LogEntry() { time="Nowish", text = "See github.com/JonathanDotCel for more info! ", Color = blackBrush });

		}

		public void AddGroup(FilterGroup inGroup)
		{

			allGroups.Add(inGroup);

			if (inGroup.isExclusion)
				exclusionGroup.Add(inGroup);
			else
				inclusionGroup.Add(inGroup);
		}

		// MVVM would be a wee bit overkill here...
		private void CBEnabled_Clicked(object sender, RoutedEventArgs e)
		{
			
			if ( cbEnabled.IsChecked.GetValueOrDefault() ){
				
				cbPaused.IsChecked = false;
				cbPaused.IsEnabled = true;

				// Clear the buffer or you'll get buffer entries from ~2 hours ago
				// even on terrible old devices like the Galaxy Tab4. Ever wondered where the RAM was going?

				// Logcat -c will plain not work on some devices.
				// "all" (/dev/log/all) is missing on some devices.
				// "main" may be locked.
				BTNDeviceClear_Click(null, null);
				
				// default -b main -b system
				lastProcess = LogcatWithParams("logcat -v threadtime");
				lastProcess.OutputDataReceived += new DataReceivedEventHandler( STDOut_OnDataReceived );
				lastProcess.BeginOutputReadLine();
				
				// Get the update pump started!
				cbEnabled.Dispatcher.BeginInvoke(				
					DispatcherPriority.Normal, new UpdateDelegate( Update ), lastProcess
				);
				
			} else {
				Console.WriteLine( "Closing process..." );
				cbPaused.IsChecked = true;
				cbPaused.IsEnabled = false;
				if ( !lastProcess.HasExited ){
					lastProcess?.Kill();
				}
			}

		}

		//  For clearing the logs or starting the main process
		Process LogcatWithParams( string inParams ){
			
			try{
				Process process = new Process();
				process.StartInfo.FileName = "adb";
				process.StartInfo.CreateNoWindow = true;
				process.StartInfo.Arguments = inParams;
				process.StartInfo.RedirectStandardInput = true;
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.RedirectStandardError = true;
				process.StartInfo.UseShellExecute = false;			
				process.Start();
				listBox1.Items.Add( new LogEntry(){ text = "Running adb with params: " + inParams, Color = blackBrush } );
				return process;
			} catch( Exception ) {
				MessageBox.Show( 
					"Woops! Looks like we couldn't find ADB.\n" +
					"Make sure it's on your %PATH%!\n" +
					"Exiting..." 
				);
				System.Environment.Exit(0);
				return null;
			}

		}

		// Obvious, intuitive visual feedback that there are 2 list boxes
		public void Animate()
		{

			if (animating)
			{
				splitterLerp += (splitterTarg - splitterLerp) * 0.4f;
				Thread.Sleep(1000 * 1 / 60);

				secondGrid.ColumnDefinitions[1].Width = new GridLength(splitterLerp, GridUnitType.Star);
				secondGrid.ColumnDefinitions[2].Width = new GridLength(5);
				secondGrid.ColumnDefinitions[3].Width = new GridLength(100 - splitterLerp, GridUnitType.Star);

			}
			else
			{
				splitterLerp = splitterTarg;
			}

			Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new GenericDelegate(Animate));

		}

		// NOT UI THREAD
		public void STDOut_OnDataReceived( object sender, DataReceivedEventArgs e ){
			
			ParseLog( e.Data );

		}

		public string WrapString( string inString ){
			
			if ( wrapLength == 0 ) return inString;
			if ( inString.Length < wrapLength ) return inString;

			// floor to int
			int numDivisions = inString.Length / wrapLength;
			for( int i = 0; i < numDivisions; i++ ){
				int insertPoint = (wrapLength * (i+1));
				inString = inString.Insert( insertPoint, "\n" );
			}
			return inString;

		}

		// NOT UI THREAD
		public void ParseLog( string rawString ){
			
			if ( string.IsNullOrEmpty( rawString ) )
				return;

			//Console.WriteLine( "Raw string: " + rawString );

			int timeStart = 0;
			int timeSplit = rawString.IndexOf('.', 0) + 4;
			if ( timeSplit == -1 ) goto cantParse;
			string timeString = rawString.Substring(timeStart, timeSplit);

			int pidStart = timeSplit + 3;			
			int pidSplit = rawString.IndexOf(' ', pidStart + 1);
			if ( pidSplit == -1 ) goto cantParse;
			string pidString = rawString.Substring(pidStart, pidSplit - pidStart);

			int tidStart = pidSplit + 3;			
			int tidSplit = rawString.IndexOf(' ', tidStart + 1);
			if ( tidSplit == -1 ) goto cantParse;
			string tidString = rawString.Substring(tidStart, tidSplit - tidStart);

			int tagStart = tidSplit + 3;			
			int tagSplit = rawString.IndexOf( ':', tagStart + 1 );
			if ( tagSplit == -1 ) goto cantParse;
			string tagString = rawString.Substring(tagStart, (tagSplit - tagStart) + 1);

			int levelStart = tidSplit + 1;
			string levelString = rawString.Substring( levelStart, 1 );


			bool sameTimestamp = false;

			// Same timestamp as previous means it's a multipart log entry
			// (Unity) So indent it.
			if ( lastAdded != null ){				
				if ( timeString == lastAdded.time && tagString == lastAdded.tag ){					
					sameTimestamp = true;
				}
			}

			int textStart = tagSplit + 2;			
			int textSplit = rawString.Length;
			if ( textSplit == -1 ) goto cantParse;
			string textString =  (sameTimestamp ? " || " : "" ) +  rawString.Substring(textStart, textSplit - textStart);
			
			LogEntry l = new LogEntry()
			{
				level = levelString,
				time = timeString,
				PID = pidString,
				TID = tidString,
				app = tagString, // not implemented
				tag = tagString,
				text = WrapString( textString ),
				raw = rawString				
			};

			switch( levelString ){				
				case "I" : l.Color = greenBrush; break;
				case "W": l.Color = orangeBrush; break;
				case "E": l.Color = redBrush; break;
				default: l.Color = blackBrush; break;
			}

			lastAdded = l;
			pendingLogs.Add(l);

			return;

			cantParse:

			LogEntry f = new LogEntry(){ text = rawString, Color = blackBrush };
			pendingLogs.Add( f );

		}

		// ON UI THREAD
		// Dumps the processed logs into the ListBox on the main Thread
		// see: ParseLog()
		public void Update( Process inProcess ){
			
			if ( pendingLogs.Count != 0 && !cbPaused.IsChecked.GetValueOrDefault() ){
				
				// Count them up and scroll in a oner
				// Otherwise you risk introducing small errors
				int addedTo1 = 0;
				int addedTo2 = 0;

				for( int i = 0; i < pendingLogs.Count; i++ ){

					LogEntry l = pendingLogs[0];
					pendingLogs.RemoveAt( 0 );

					// Is it excluded (from both lists)
					if (FilterMatches(exclusionGroup[0], l, false)) break;
					if (FilterMatches(exclusionGroup[1], l, false)) break;
					
					{
						listBox1.Items.Add(l);
						addedTo1++;						
					}

					if ( ValidEntry( l ) )
					{
						listBox2.Items.Add(l);
						addedTo2++;						
					}

					if (pendingLogs.Count == 0)
					{
						break;
					}


				}

				Prune( listBox1 );
				Scroll( listBox1, addedTo1 );

				Prune( listBox2 );
				Scroll( listBox2, addedTo2 );

			}

			if (lastProcess.HasExited && cbEnabled.IsChecked.GetValueOrDefault())
			{
				cbEnabled.IsChecked = false;
				LogEntry l = new LogEntry(){ text = "ADB terminated with exit code " + inProcess?.ExitCode, Color = blackBrush };
				listBox1.Items.Add( l );
				listBox2.Items.Add( l );
			}

			if ( cbEnabled.IsChecked.GetValueOrDefault() ){
				Application.Current.Dispatcher.BeginInvoke(
					DispatcherPriority.ApplicationIdle, new UpdateDelegate(Update), inProcess
				);
			}
			
			// Garbage collector's kinda great, huh?			
			lblBuffer.Content = "Unfiltered buffer: " + listBox1.Items.Count + "/" + bufferSize + " Filtered buffer: " + listBox2.Items.Count + "/" + bufferSize;

		} //Blah

		// Should this LogEntry be added to the filtered list?
		bool ValidEntry( LogEntry inEntry ){

			// Don't go into filtered if there's nothing filtered
			if ( !_anyFilterEnabled ) return false;

			
			if ( filterMode == FilterMode.ALL ){
				// Simpler to unroll this while we just use 4 filters
				return ( 
					FilterMatches( inclusionGroup[0], inEntry, true )
					&& FilterMatches( inclusionGroup[1], inEntry, true )
					&& FilterMatches( inclusionGroup[2], inEntry, true )
					&& FilterMatches( inclusionGroup[3], inEntry, true )
				);
			} 

			if ( filterMode == FilterMode.ANY ){
				// Simpler to unroll this while we just use 4 filters				
				if (FilterMatches( inclusionGroup[0], inEntry )) return true;				
				if (FilterMatches( inclusionGroup[1], inEntry )) return true;				
				if (FilterMatches( inclusionGroup[2], inEntry )) return true;
				if (FilterMatches( inclusionGroup[3], inEntry )) return true;
				return false;
			}
			
			return false;

		}

		// Checks a given log entry against the Inclusion filters or the Exclusion filters
		// FilterMode.ANY: return false if unchecked
		// FilterMode.ALL: return true if unchecked
		bool FilterMatches( FilterGroup inGroup, LogEntry inEntry, bool uncheckedReturnValue = false ){
			
			// this filter isn't active, so it's not blocking anything
			if ( !inGroup.isEnabled ) return uncheckedReturnValue;
			
			string userFilter = inGroup.text;

			// User's searching for level
			int idx = userFilter.IndexOf("level:",StringComparison.OrdinalIgnoreCase);
			if ( idx == 0 && !string.IsNullOrEmpty( inEntry.level ) ){
				return inEntry.level.IndexOf( userFilter.Substring( 0, 5 ) ) > -1;
			}

			idx = userFilter.IndexOf("pid:", StringComparison.OrdinalIgnoreCase);
			if ( idx == 0 && !string.IsNullOrEmpty( inEntry.PID ) ){				
				return inEntry.PID.IndexOf( userFilter.Substring( 4, userFilter.Length -4 ) ) > -1;
			}

			idx = userFilter.IndexOf("tid:", StringComparison.OrdinalIgnoreCase);
			if ( idx == 0 && !string.IsNullOrEmpty( inEntry.TID ) ){				
				return inEntry.TID.IndexOf( userFilter.Substring( 4, userFilter.Length -4 ) ) > -1;
			}

			idx = userFilter.IndexOf("app:", StringComparison.OrdinalIgnoreCase);
			if ( idx == 0 && !string.IsNullOrEmpty( inEntry.tag ) ){
				return inEntry.app.IndexOf( userFilter.Substring( 4, userFilter.Length -4 ) ) > -1;
			}

			idx = userFilter.IndexOf("tag:", StringComparison.OrdinalIgnoreCase);
			if ( idx == 0 && !string.IsNullOrEmpty( inEntry.tag ) ){
				return inEntry.tag.IndexOf( userFilter.Substring( 4, userFilter.Length -4 ) ) > -1;
			}

			idx = userFilter.IndexOf("text:", StringComparison.OrdinalIgnoreCase);
			if ( idx == 0 && !string.IsNullOrEmpty( inEntry.text ) )
			{				
				return inEntry.text.IndexOf( userFilter.Substring( 5, userFilter.Length -5) ) > -1;
			}

			if ( string.IsNullOrEmpty( inEntry.raw ) )
				return false;
			
			idx = inEntry.raw.IndexOf( userFilter, StringComparison.OrdinalIgnoreCase );			
			return ( idx > -1 );
			
		}


		// Lots of individual scroll calls will be lost in the chop
		// so count them up and apply in one shot where possible
		void Scroll( ListBox inBox, int howManyLines = 1 ){

			if ( howManyLines == 0 ) return;

			if ( cbScroll.IsChecked.GetValueOrDefault() )
			{
				// The shit you have to deal with in WPF...
				inBox.SelectedIndex = inBox.Items.Count - 1;
				GetScroll( inBox ).ScrollToBottom();

				// Alternate method incase the hierarchy breaks at some point
				// ( MoveCurrentToLast stuff is required )
				//inBox.Items.MoveCurrentToLast();				
				//inBox.ScrollIntoView( inBox.Items.CurrentItem );

			} else {
				
				GetScroll( inBox ).ScrollToVerticalOffset( GetScroll(inBox).VerticalOffset - howManyLines );
								
			}

		}

		// The hoops we have to jump through with WPF sometimes...
		ScrollViewer GetScroll( ListBox inBox ){			
			Decorator border = (Decorator)VisualTreeHelper.GetChild( inBox, 0 );			
			ScrollViewer sv = (ScrollViewer)VisualTreeHelper.GetChild( border, 0 );
			return sv;			
		}

		void Prune( ListBox inBox ){
			while( inBox.Items.Count > bufferSize ){
				inBox.Items.RemoveAt( 0 );
			}
		}

		// Clear either or both list boxes
		private void BTNClear_Click(object sender, RoutedEventArgs e)
		{
			if ( !anyFilterEnabled || cbSplitView.IsChecked.GetValueOrDefault() ) listBox1.Items.Clear();
			if ( anyFilterEnabled || cbSplitView.IsChecked.GetValueOrDefault()) listBox2.Items.Clear();
		}

		// Not all will succeed on every devices
		// so separate processes get the job done
		private void BTNDeviceClear_Click(object sender, RoutedEventArgs e ){
			LogcatWithParams("logcat -b main -c");
			LogcatWithParams("logcat -b system -c");
			LogcatWithParams("logcat -b radio -c");
			LogcatWithParams("logcat -b events -c");
			LogcatWithParams("logcat -b all -c");
		}
		

		private void UpdateFilters(object sender, RoutedEventArgs e){
			
			// update the cache
			// TODO: not this
			_anyFilterEnabled = anyFilterEnabled;
			
			if ( cbSplitView.IsChecked.GetValueOrDefault() ){				
				splitterTarg = 50;
			} else {				
				splitterTarg = anyFilterEnabled ? 2 : 98;
			}

		}

		
		private void CheckBox_Click(object sender, RoutedEventArgs e)
		{
			filterMode = ( filterMode == FilterMode.ANY ? FilterMode.ALL : FilterMode.ANY );
		}

		// Would be nice if WPF had chosen GridLength instead of "double" for GridViewColumn.Widths		
		private void ListViewSizeChanged(object sender, SizeChangedEventArgs e)
		{
			ListView lv = (ListView)sender;
			GridView gv = (GridView)(lv.View);
			// ty Gary Connell, Konraw Morawski for the scrollbar hint!
			double listWidth = lv.ActualWidth - SystemParameters.VerticalScrollBarWidth;
			double columnsWidth = 0;
			for( int i = 0; i < gv.Columns.Count -1; i++ ){
				columnsWidth -= gv.Columns[i].ActualWidth;
			}
			gv.Columns[ gv.Columns.Count-1 ].Width = listWidth - columnsWidth;
		}
		

		// disable filter boxes while typing
		// until the user hits Enter
		private void txtFilter_KeyUp(object sender, KeyEventArgs e)
		{
			TextBox whichBox = (TextBox)sender;
			
			// Still feels dirty using Linq for this...
			FilterGroup f = 
				( from grp in allGroups
				where grp.textBox == whichBox
				select grp).FirstOrDefault();
			
			bool isEnter = e.Key == Key.Enter;
			bool empty = string.IsNullOrEmpty(whichBox.Text);

			f.checkBox.IsChecked = (isEnter && !empty);
			UpdateFilters(null, null);

		}


		// Limits the input to integers, but doesn't (yet) validate ranges.
		// Remember in like 1999, Delphi, Visual Basic, etc had this boilerplate busywork covered?
		public void IntegerLimit( TextBox inBox, int defaultValue ){
			
			// called during initialisecomponent
			if ( inBox == null ) return;

			if ( !int.TryParse( inBox.Text, out int dummyValue ) ){
				int oldSelection = inBox.SelectionStart;
				string newString = "";
				for( int i = 0; i < inBox.Text.Length; i++ ){
					if ( (byte)inBox.Text[i] >= '0' && (byte)inBox.Text[i] <= '9' )
						newString += inBox.Text[i];
				}
				if ( string.IsNullOrEmpty( newString ) )
					newString = defaultValue.ToString();
				
				// Put it back 'cause we're about to re-parse
				inBox.Text = newString;

				// E.g. when pasting over the existing value
				inBox.SelectionStart = Math.Min( oldSelection, newString.Length );
			}
		}

		// 0 for no max
		public int FinaliseTextbox( TextBox inBox, int inMin, int inMax ){

			// guaranteed to be a parsable int already via IntegerLimit() callbacks
			
			// parse the new value
			int returnVal;
			int.TryParse( inBox.Text, out returnVal );

			bool updateSelection = false;
			// then clamp it between whatever and whatever else
			if (returnVal < inMin)
			{
				returnVal = inMin;
				updateSelection = true;
			}

			if (inMax != 0 && returnVal > inMax)
			{
				returnVal = inMax;
				updateSelection = true;
			}

			// finally, actually set the value post clamping
			inBox.Text = returnVal.ToString();

			if (updateSelection)
				inBox.SelectionStart = inBox.Text.Length - 1;

			return returnVal;
			
		}

		// The user is typing. Limit the input, but don't set the final variable yet.
		private void TextBox_Changed( object sender, TextChangedEventArgs e){
			IntegerLimit( txtWrapLength, 100 );
			IntegerLimit( txtBufferSize, 1000 );
		}

		// The user has pushed enter or left the text box, clamp and apply the value
		private void TextBox_KeyUp(object sender, KeyEventArgs e)
		{
			if ( e.Key == Key.Enter ) TextBox_LostFocus( sender, null );			
		}

		// The user has pushed enter or left the text box, clamp and apply the value
		private void TextBox_LostFocus(object sender, RoutedEventArgs e)
		{
			bufferSize = FinaliseTextbox( txtBufferSize, 100, 100000 );
			wrapLength = FinaliseTextbox( txtWrapLength, 0, 0 );
		}


	}


}